using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Java;
using SubReel.Launcher.Application.Loaders;
using SubReel.Launcher.Domain.Java;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Minecraft.Layout;
using SubReel.Launcher.Minecraft.Java;
using SubReel.Launcher.Minecraft.Versions;

namespace SubReel.Launcher.Minecraft.Loaders;

/// <summary>
/// Ставит Fabric/Quilt, Forge и NeoForge в общий кэш игры. Fabric и Quilt получают профиль с мета-сервера; современные Forge/NeoForge
/// запускают официальный клиентский установщик с processors; старый Forge извлекает профиль и встроенные библиотеки из installer JAR.
/// Профиль (version.json с <c>inheritsFrom</c>) сохраняется в кэше как обычная версия
/// (<c>versions/&lt;профиль&gt;/&lt;профиль&gt;.json</c>). Дальше работает обычная установка версии: <c>GameInstaller</c> по цепочке
/// <c>inheritsFrom</c> берёт ванильного родителя, а библиотеки загрузчика (имя + адрес Maven-репозитория) скачивает по тем же правилам.
/// Скачанный профиль проверяется до того, как лечь на место: годный JSON, родитель — именно эта версия игры, есть главный класс.
/// </summary>
public sealed class LoaderInstaller : ILoaderInstaller
{
    private readonly ILoaderCatalog _catalog;
    private readonly IDownloadManager _downloads;
    private readonly GameFileLayout _layout;
    private readonly LoaderEndpoints _endpoints;
    private readonly ILogger<LoaderInstaller> _logger;
    private readonly IJavaManager? _java;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LoaderInstaller(ILoaderCatalog catalog, IDownloadManager downloads, GameFileLayout layout, LoaderEndpoints endpoints, ILogger<LoaderInstaller> logger, IJavaManager? java = null)
    {
        _catalog = catalog;
        _downloads = downloads;
        _layout = layout;
        _endpoints = endpoints;
        _logger = logger;
        _java = java;
    }

    public async Task<LoaderInstallResult> EnsureAsync(LoaderKind kind, string mcVersion, string? loaderVersion, CancellationToken ct = default)
    {
        if (!LoaderInfo.CanInstall(kind))
            return LoaderInstallResult.Failure($"Загрузчик {LoaderInfo.Name(kind)} лаунчер пока не умеет устанавливать.");

        mcVersion = mcVersion?.Trim() ?? "";
        if (!LoaderMetaParser.IsSafeVersionText(mcVersion))
            return LoaderInstallResult.Failure($"Недопустимая версия игры «{mcVersion}».");

        loaderVersion = string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion.Trim();
        if (loaderVersion is not null && !LoaderMetaParser.IsSafeVersionText(loaderVersion))
            return LoaderInstallResult.Failure($"Недопустимая версия загрузчика «{loaderVersion}».");

        // Две одновременные установки одного профиля писали бы в один временный файл.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (loaderVersion is null)
            {
                var latest = await PickLatestAsync(kind, mcVersion, ct).ConfigureAwait(false);
                if (latest.Error is not null) return LoaderInstallResult.Failure(latest.Error);
                loaderVersion = latest.Version!;
            }

            var id = LoaderInfo.ProfileId(kind, mcVersion, loaderVersion);

            string path;
            try
            {
                path = _layout.VersionJsonPath(id);
            }
            catch (VersionMetadataException ex)
            {
                return LoaderInstallResult.Failure(ex.Message);
            }

            if (kind == LoaderKind.Forge && !LoaderMetaParser.IsForgeSupportedGame(mcVersion))
                return LoaderInstallResult.Failure($"Forge для Minecraft {mcVersion} лаунчер не устанавливает: поддерживаются версии от 1.12.2.");

            if (kind is LoaderKind.NeoForge or LoaderKind.Forge)
            {
                if (IsUsableProfile(path, mcVersion)) return LoaderInstallResult.Success(id, loaderVersion);
                if (kind == LoaderKind.Forge && !LoaderMetaParser.IsForgeModernVersion(mcVersion))
                    return await InstallLegacyForgeAsync(mcVersion, loaderVersion, id, path, ct).ConfigureAwait(false);
                return await InstallInstallerLoaderAsync(kind, mcVersion, loaderVersion, id, path, ct).ConfigureAwait(false);
            }

            if (IsUsableProfile(path, mcVersion)) return LoaderInstallResult.Success(id, loaderVersion);

            return await DownloadProfileAsync(kind, mcVersion, loaderVersion, id, path, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LoaderInstallResult> InstallLegacyForgeAsync(string mcVersion, string loaderVersion, string id, string profilePath, CancellationToken ct)
    {
        var coordinate = $"{mcVersion}-{loaderVersion}";
        var installerPath = Path.Combine(_layout.Root, "loaders", "forge", $"forge-{coordinate}-installer.jar");
        var tempInstaller = installerPath + ".download";
        var profileTemp = profilePath + ".installing";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(installerPath)!);
            if (!File.Exists(installerPath))
            {
                var downloadError = await DownloadInstallerAsync(_endpoints.ForgeInstallerUrl(mcVersion, loaderVersion), tempInstaller, ct).ConfigureAwait(false);
                if (downloadError is not null) return LoaderInstallResult.Failure($"Не удалось скачать установщик Forge {loaderVersion}: {downloadError}");
                File.Move(tempInstaller, installerPath, overwrite: true);
            }

            ZipArchive archive;
            try { archive = ZipFile.OpenRead(installerPath); }
            catch (InvalidDataException)
            {
                TryDelete(installerPath); // битый файл в кэше: при следующей попытке скачается заново
                return LoaderInstallResult.Failure($"Установщик Forge {loaderVersion} повреждён и удалён из кэша — повтори попытку.");
            }
            using var archiveScope = archive;
            var profileJson = await ReadLegacyVersionJsonAsync(archive, ct).ConfigureAwait(false);
            if (profileJson is null) return LoaderInstallResult.Failure("В установщике старого Forge не найден version.json или versionInfo.");
            var metadata = VersionMetadataParser.Parse(profileJson);
            if (string.IsNullOrWhiteSpace(metadata.InheritsFrom))
                return LoaderInstallResult.Failure($"Forge {loaderVersion} для Minecraft {mcVersion} — самый старый формат (до 1.12.2): профиль без inheritsFrom и библиотеки вне maven/ установщика пока не поддерживаются.");
            if (!string.Equals(metadata.Id, id, StringComparison.Ordinal) || !string.Equals(metadata.InheritsFrom, mcVersion, StringComparison.Ordinal))
                return LoaderInstallResult.Failure("Установщик Forge вернул профиль для другой версии Minecraft.");
            if (string.IsNullOrWhiteSpace(metadata.MainClass)) return LoaderInstallResult.Failure("В профиле Forge не указан главный класс.");

            foreach (var library in metadata.Libraries)
            {
                ct.ThrowIfCancellationRequested();
                if (library.Artifact is not { Download.Url.Length: 0 } artifact) continue;
                var embedded = archive.GetEntry("maven/" + artifact.Path.Replace('\\', '/'));
                if (embedded is null) return LoaderInstallResult.Failure($"В установщике Forge отсутствует встроенная библиотека «{artifact.Path}».");
                var destination = _layout.LibraryPath(artifact.Path);
                var tempLibrary = destination + ".installing";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using (var source = embedded.Open())
                    await using (var target = new FileStream(tempLibrary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                        await source.CopyToAsync(target, ct).ConfigureAwait(false);

                    var file = new FileInfo(tempLibrary);
                    if (artifact.Download.Size is { } size && file.Length != size)
                        return LoaderInstallResult.Failure($"Встроенная библиотека Forge «{artifact.Path}» имеет неверный размер.");
                    if (artifact.Download.Sha1 is { Length: 40 } expected)
                    {
                        await using var stream = File.OpenRead(tempLibrary);
                        var hash = Convert.ToHexString(await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false));
                        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                            return LoaderInstallResult.Failure($"Встроенная библиотека Forge «{artifact.Path}» не прошла проверку SHA-1.");
                    }
                    File.Move(tempLibrary, destination, overwrite: true);
                }
                finally { TryDelete(tempLibrary); }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
            await File.WriteAllTextAsync(profileTemp, profileJson, ct).ConfigureAwait(false);
            File.Move(profileTemp, profilePath, overwrite: true);
            if (!IsUsableProfile(profilePath, mcVersion)) return LoaderInstallResult.Failure("Профиль старого Forge не прошёл проверку после установки.");
            _logger.LogInformation("Старый Forge {LoaderVersion} для Minecraft {Version} установлен без запуска processors.", loaderVersion, mcVersion);
            return LoaderInstallResult.Success(id, loaderVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or VersionMetadataException or JsonException)
        {
            _logger.LogError(ex, "Не удалось установить старый Forge {LoaderVersion} для Minecraft {Version}.", loaderVersion, mcVersion);
            return LoaderInstallResult.Failure($"Не удалось установить старый Forge: {ex.Message}");
        }
        finally { TryDelete(tempInstaller); TryDelete(profileTemp); }
    }

    /// <summary>
    /// Скачивает установщик и сверяет SHA-1 с файлом <c>&lt;адрес&gt;.sha1</c>, который Maven кладёт рядом.
    /// Установщик запускается как программа, поэтому без подтверждённого хэша он не используется.
    /// </summary>
    /// <returns>null — файл скачан и проверен, иначе причина отказа.</returns>
    private async Task<string?> DownloadInstallerAsync(string url, string destination, CancellationToken ct)
    {
        var shaPath = destination + ".sha1";
        try
        {
            var sha = await _downloads.DownloadAsync(new DownloadRequest(url + ".sha1", shaPath), null, ct).ConfigureAwait(false);
            if (!sha.Succeeded) return $"не получена контрольная сумма: {sha.Error}";

            var text = (await File.ReadAllTextAsync(shaPath, ct).ConfigureAwait(false)).Trim();
            var expected = text.Split(new[] { ' ', '\t', '\r', '\n' }, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (expected.Length != 40 || !expected.All(Uri.IsHexDigit)) return "контрольная сумма на сервере записана в неожиданном формате.";

            var result = await _downloads.DownloadAsync(new DownloadRequest(url, destination, expected), null, ct).ConfigureAwait(false);
            return result.Succeeded ? null : result.Error ?? "загрузка не удалась.";
        }
        finally { TryDelete(shaPath); }
    }

    private static async Task<string?> ReadLegacyVersionJsonAsync(ZipArchive archive, CancellationToken ct)
    {
        var version = archive.GetEntry("version.json");
        if (version is not null)
        {
            using var reader = new StreamReader(version.Open());
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        var installProfile = archive.GetEntry("install_profile.json");
        if (installProfile is null) return null;
        using var profileReader = new StreamReader(installProfile.Open());
        using var document = JsonDocument.Parse(await profileReader.ReadToEndAsync(ct).ConfigureAwait(false));
        if (document.RootElement.TryGetProperty("versionInfo", out var info) && info.ValueKind == JsonValueKind.Object)
            return info.GetRawText();
        if (document.RootElement.TryGetProperty("json", out var jsonPath) && jsonPath.ValueKind == JsonValueKind.String)
        {
            var name = jsonPath.GetString()?.TrimStart('/');
            var entry = string.IsNullOrWhiteSpace(name) ? null : archive.GetEntry(name);
            if (entry is null) return null;
            using var reader = new StreamReader(entry.Open());
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<LoaderInstallResult> InstallInstallerLoaderAsync(LoaderKind kind, string mcVersion, string loaderVersion, string id, string profilePath, CancellationToken ct)
    {
        var loaderName = LoaderInfo.Name(kind);
        if (_java is null) return LoaderInstallResult.Failure($"Для установки {loaderName} нужен Java Manager.");
        var artifactName = kind == LoaderKind.NeoForge ? $"neoforge-{loaderVersion}" : $"forge-{mcVersion}-{loaderVersion}";
        var installerPath = Path.Combine(_layout.Root, "loaders", kind.ToString().ToLowerInvariant(), artifactName + "-installer.jar");
        var tempInstaller = installerPath + ".download";
        var profileTemp = profilePath + ".installing";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(installerPath)!);
            if (!File.Exists(installerPath))
            {
                var url = kind == LoaderKind.NeoForge ? _endpoints.NeoForgeInstallerUrl(loaderVersion) : _endpoints.ForgeInstallerUrl(mcVersion, loaderVersion);
                var downloadError = await DownloadInstallerAsync(url, tempInstaller, ct).ConfigureAwait(false);
                if (downloadError is not null) return LoaderInstallResult.Failure($"Не удалось скачать установщик {loaderName} {loaderVersion}: {downloadError}");
                File.Move(tempInstaller, installerPath, overwrite: true);
            }

            string versionJson;
            ZipArchive opened;
            try { opened = ZipFile.OpenRead(installerPath); }
            catch (InvalidDataException)
            {
                TryDelete(installerPath);
                return LoaderInstallResult.Failure($"Установщик {loaderName} {loaderVersion} повреждён и удалён из кэша — повтори попытку.");
            }
            using (var archive = opened)
            {
                var entry = archive.GetEntry("version.json") ?? throw new InvalidDataException($"В установщике {loaderName} нет version.json. Возможно, этот выпуск ещё не использует современный формат.");
                using var reader = new StreamReader(entry.Open());
                versionJson = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            var metadata = VersionMetadataParser.Parse(versionJson);
            if (!string.Equals(metadata.Id, id, StringComparison.Ordinal) || !string.Equals(metadata.InheritsFrom, mcVersion, StringComparison.Ordinal))
                return LoaderInstallResult.Failure($"Установщик {loaderName} вернул профиль для другой версии Minecraft.");
            if (string.IsNullOrWhiteSpace(metadata.MainClass)) return LoaderInstallResult.Failure($"В профиле {loaderName} не указан главный класс.");

            var (defaultJavaMajor, defaultJavaComponent) = DefaultJava(mcVersion);
            var javaRequirement = new JavaRequirement(metadata.JavaMajorVersion ?? defaultJavaMajor, metadata.JavaComponent ?? defaultJavaComponent);
            var java = await _java.EnsureAsync(javaRequirement, ct: ct).ConfigureAwait(false);
            if (!java.Succeeded) return LoaderInstallResult.Failure(java.Error ?? $"Не найдена подходящая Java для {loaderName}.");

            var start = new ProcessStartInfo(java.Installation!.Executable)
            {
                WorkingDirectory = _layout.Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-jar");
            start.ArgumentList.Add(installerPath);
            start.ArgumentList.Add(kind == LoaderKind.NeoForge ? "--install-client" : "--installClient");
            start.ArgumentList.Add(_layout.Root);
            EnsureLauncherProfilesFile();
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"Не удалось запустить установщик {loaderName}.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                return LoaderInstallResult.Failure($"Установщик {loaderName} не завершился за 20 минут.");
            }
            var output = (await stdout.ConfigureAwait(false)) + Environment.NewLine + (await stderr.ConfigureAwait(false));
            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Установщик {Loader} {Version} завершился с кодом {ExitCode}: {Output}", loaderName, loaderVersion, process.ExitCode, LastUsefulLines(output));
                return LoaderInstallResult.Failure($"Установщик {loaderName} завершился с кодом {process.ExitCode}: {LastUsefulLines(output)}");
            }

            // The installer writes the profile itself. Revalidate it before treating the installation as complete.
            if (!IsUsableProfile(profilePath, mcVersion))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
                await File.WriteAllTextAsync(profileTemp, versionJson, ct).ConfigureAwait(false);
                File.Move(profileTemp, profilePath, overwrite: true);
            }
            if (!IsUsableProfile(profilePath, mcVersion)) return LoaderInstallResult.Failure($"Установщик {loaderName} не создал пригодный профиль версии.");
            _logger.LogInformation("{Loader} {LoaderVersion} для Minecraft {Version} установлен.", loaderName, loaderVersion, mcVersion);
            return LoaderInstallResult.Success(id, loaderVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or InvalidDataException or VersionMetadataException or JsonException)
        {
            _logger.LogError(ex, "Не удалось установить {Loader} {LoaderVersion} для Minecraft {Version}.", loaderName, loaderVersion, mcVersion);
            return LoaderInstallResult.Failure($"Не удалось установить {loaderName}: {ex.Message}");
        }
        finally { TryDelete(tempInstaller); TryDelete(profileTemp); }
    }

    private void EnsureLauncherProfilesFile()
    {
        var standard = Path.Combine(_layout.Root, "launcher_profiles.json");
        var microsoft = Path.Combine(_layout.Root, "launcher_profiles_microsoft_store.json");
        if (File.Exists(standard) || File.Exists(microsoft)) return;
        Directory.CreateDirectory(_layout.Root);
        File.WriteAllText(standard, "{\"profiles\":{}}\n");
    }

    private static (int Major, string Component) DefaultJava(string mcVersion)
    {
        if (LoaderMetaParser.TryParseRelease(mcVersion, out var major, out var minor))
        {
            if (major <= 16) return (8, "jre-legacy");
            if (major == 17) return (16, "java-runtime-alpha");
            if (major < 20 || major == 20 && minor < 5) return (17, "java-runtime-gamma");
            if (major >= 26) return (25, "java-runtime-epsilon");
        }
        return (21, "java-runtime-delta");
    }

    private static string LastUsefulLines(string output) => string.Join(" | ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(4));

    private async Task<(string? Version, string? Error)> PickLatestAsync(LoaderKind kind, string mcVersion, CancellationToken ct)
    {
        IReadOnlyList<LoaderBuild> builds;
        try
        {
            builds = await _catalog.GetBuildsAsync(kind, mcVersion, ct).ConfigureAwait(false);
        }
        catch (LoaderCatalogException ex)
        {
            _logger.LogWarning(ex, "Не удалось узнать последнюю сборку {Loader} для Minecraft {Version}.", kind, mcVersion);
            return (null, ex.Message);
        }

        var pick = builds.FirstOrDefault(b => b.Stable) ?? builds.FirstOrDefault();
        return pick is null
            ? (null, $"{LoaderInfo.Name(kind)} не поддерживает Minecraft {mcVersion}.")
            : (pick.Version, null);
    }

    private async Task<LoaderInstallResult> DownloadProfileAsync(LoaderKind kind, string mcVersion, string loaderVersion, string id, string path, CancellationToken ct)
    {
        var temp = path + ".download";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var url = _endpoints.ProfileUrl(kind, mcVersion, loaderVersion);
            var result = await _downloads.DownloadAsync(new DownloadRequest(url, temp), null, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logger.LogWarning("Профиль {Loader} {LoaderVersion} для Minecraft {Version} не скачан: {Error}", kind, loaderVersion, mcVersion, result.Error);
                return LoaderInstallResult.Failure(
                    $"Не удалось скачать профиль {LoaderInfo.Name(kind)} {loaderVersion} для Minecraft {mcVersion}: {result.Error}");
            }

            var problem = CheckProfile(File.ReadAllText(temp), mcVersion);
            if (problem is not null)
            {
                _logger.LogWarning("Профиль {Id} отклонён: {Problem}", id, problem);
                return LoaderInstallResult.Failure($"Мета-сервер {LoaderInfo.Name(kind)} вернул неподходящий профиль: {problem}");
            }

            File.Move(temp, path, overwrite: true);
            _logger.LogInformation("Профиль загрузчика {Id} установлен.", id);
            return LoaderInstallResult.Success(id, loaderVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Ошибка диска при установке профиля {Id}.", id);
            return LoaderInstallResult.Failure($"Ошибка записи на диск: {ex.Message}");
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>Профиль на диске читается и подходит версии игры; иначе его скачают заново.</summary>
    private static bool IsUsableProfile(string path, string mcVersion)
    {
        if (!File.Exists(path)) return false;
        try
        {
            return CheckProfile(File.ReadAllText(path), mcVersion) is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <returns>null — профиль годный, иначе причина отказа.</returns>
    private static string? CheckProfile(string json, string mcVersion)
    {
        GameVersionMetadata metadata;
        try
        {
            metadata = VersionMetadataParser.Parse(json);
        }
        catch (VersionMetadataException ex)
        {
            return ex.Message;
        }

        if (!string.Equals(metadata.InheritsFrom, mcVersion, StringComparison.Ordinal))
            return $"он основан на «{metadata.InheritsFrom ?? "—"}», а нужна Minecraft {mcVersion}.";
        if (string.IsNullOrWhiteSpace(metadata.MainClass)) return "в нём не указан главный класс.";
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // временный файл перезапишется при следующей установке
        }
    }
}
