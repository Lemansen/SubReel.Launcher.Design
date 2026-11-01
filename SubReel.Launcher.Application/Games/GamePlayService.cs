using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Instances;
using SubReel.Launcher.Application.Loaders;
using SubReel.Launcher.Domain.Accounts;
using SubReel.Launcher.Domain.Instances;
using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Application.Games;

/// <summary>Что нажал пользователь: версия из нижней панели, загрузчик (и его версия, если выбрана) и активный аккаунт.</summary>
/// <param name="LoaderVersion">Версия загрузчика. null у ванили; у Fabric, Quilt, Forge и NeoForge null означает «последняя стабильная».</param>
public sealed record PlayRequest(string McVersion, LoaderKind Loader, LauncherAccount Account, string? LoaderVersion = null);

/// <summary>Итог нажатия «Играть». <see cref="Instance"/> — сборка, под которую шёл запуск (null, если до неё дело не дошло).</summary>
public sealed record PlayResult(LaunchResult Launch, Instance? Instance);

/// <summary>
/// Сценарий кнопки «Играть»: для выбранной версии находит свою сборку (экземпляр) или создаёт её, запускает игру в папке этой сборки
/// и отмечает время последней игры. Сборка, созданная здесь, называется «Minecraft &lt;версия&gt;» (с загрузчиком — «Minecraft &lt;версия&gt; Fabric»);
/// пользователь потом сможет её переименовать на экране экземпляров, после чего при следующем запуске создастся новая.
/// Saves, mods и options.txt лежат в папке сборки, библиотеки и ассеты — в общем кэше, поэтому вторая сборка не занимает лишнего места.
/// Fabric и Quilt: перед запуском профиль загрузчика кладётся в общий кэш, дальше он запускается как обычная версия с родителем-ванилью.
/// Одна сборка на пару «версия игры + загрузчик»: смена версии загрузчика обновляет запись сборки, а не создаёт новую (моды и миры остаются).
/// </summary>
public sealed class GamePlayService
{
    /// <summary>Сколько вариантов имени пробовать («Minecraft 1.21.1», «… (2)», …), если имя занято сборкой с другой версией.</summary>
    private const int MaxNameAttempts = 20;

    private readonly InstanceService _instances;
    private readonly IGameLauncher _launcher;
    private readonly ILoaderInstaller _loaders;
    private readonly ILogger<GamePlayService> _logger;

    public GamePlayService(InstanceService instances, IGameLauncher launcher, ILoaderInstaller loaders, ILogger<GamePlayService> logger)
    {
        _instances = instances;
        _launcher = launcher;
        _loaders = loaders;
        _logger = logger;
    }

    public static string AutoName(string mcVersion) => $"Minecraft {mcVersion.Trim()}";

    public static string AutoName(string mcVersion, LoaderKind loader) =>
        loader == LoaderKind.Vanilla ? AutoName(mcVersion) : $"{AutoName(mcVersion)} {LoaderInfo.Name(loader)}";

    public async Task<PlayResult> PlayAsync(PlayRequest request, IProgress<LaunchProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.McVersion)) throw new ArgumentException("Не указана версия игры.", nameof(request));

        var mcVersion = request.McVersion.Trim();
        var loader = request.Loader;

        // Не создаём сборку, которую всё равно нельзя запустить.
        if (loader != LoaderKind.Vanilla && !LoaderInfo.CanInstall(loader))
            return new PlayResult(LaunchResult.Fail(LaunchFailure.UnsupportedLoader, UnsupportedText(loader)), null);

        var versionId = mcVersion;
        string? loaderVersion = null;

        if (loader != LoaderKind.Vanilla)
        {
            progress?.Report(new LaunchProgress(LaunchStage.PreparingLoader, 0));
            var prepared = await _loaders.EnsureAsync(loader, mcVersion, request.LoaderVersion, ct).ConfigureAwait(false);
            if (!prepared.Succeeded || prepared.VersionId is null || prepared.LoaderVersion is null)
            {
                return new PlayResult(
                    LaunchResult.Fail(LaunchFailure.LoaderNotReady, prepared.Error ?? $"Не удалось подготовить загрузчик {LoaderInfo.Name(loader)}."),
                    null);
            }

            versionId = prepared.VersionId;
            loaderVersion = prepared.LoaderVersion;
        }

        var instance = await FindOrCreateAsync(mcVersion, loader, loaderVersion, ct).ConfigureAwait(false);
        if (instance is null)
        {
            return new PlayResult(
                LaunchResult.Fail(LaunchFailure.InstanceUnavailable, "Не удалось подготовить папку сборки. Проверь, что папка данных лаунчера доступна для записи."),
                null);
        }

        var launchRequest = LaunchRequest.ForInstance(instance, _instances.GetGameDirectory(instance.Id), request.Account) with { VersionId = versionId };
        var launch = await _launcher.LaunchAsync(launchRequest, progress, ct).ConfigureAwait(false);

        if (launch.Succeeded) await MarkPlayedAsync(instance).ConfigureAwait(false);
        return new PlayResult(launch, instance);
    }

    private static string UnsupportedText(LoaderKind loader) => loader switch
    {
        LoaderKind.Other => "Особые сборки (OptiFine, LabyMod и подобные) запустить из лаунчера пока нельзя.",
        _ => "Этот загрузчик пока не поддерживается.",
    };

    private async Task<Instance?> FindOrCreateAsync(string mcVersion, LoaderKind loader, string? loaderVersion, CancellationToken ct)
    {
        var baseName = AutoName(mcVersion, loader);

        var existing = (await _instances.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(i =>
            i.Loader == loader
            && string.Equals(i.McVersion, mcVersion, StringComparison.Ordinal)
            && string.Equals(i.Name.Trim(), baseName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return await RememberLoaderVersionAsync(existing, loaderVersion, ct).ConfigureAwait(false);

        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var name = attempt == 1 ? baseName : $"{baseName} ({attempt})";
            var created = await _instances.CreateAsync(name, mcVersion, loader, loaderVersion, ct).ConfigureAwait(false);
            if (created.Succeeded) return created.Instance;
            if (created.StorageFailed || created.Error != InstanceValidationError.NameDuplicate) break;
        }

        _logger.LogError("Не удалось создать сборку для Minecraft {Version} ({Loader}).", mcVersion, loader);
        return null;
    }

    /// <summary>Сборка запускается с другой версией загрузчика, чем в прошлый раз: обновляем запись, мир и моды остаются на месте.</summary>
    private async Task<Instance> RememberLoaderVersionAsync(Instance instance, string? loaderVersion, CancellationToken ct)
    {
        if (loaderVersion is null || string.Equals(instance.LoaderVersion, loaderVersion, StringComparison.Ordinal)) return instance;

        var changed = await _instances.SetLoaderVersionAsync(instance.Id, loaderVersion, ct).ConfigureAwait(false);
        if (changed.Succeeded) return changed.Instance!;

        // Игре версия загрузчика нужна из запроса, а не из записи сборки: запуск продолжаем.
        _logger.LogWarning("Не удалось запомнить версию загрузчика {Version} для сборки {Id}.", loaderVersion, instance.Id);
        return instance;
    }

    private async Task MarkPlayedAsync(Instance instance)
    {
        try
        {
            // Игра уже запущена: отмена подготовки не должна отменять эту отметку.
            await _instances.MarkPlayedAsync(instance.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось отметить время запуска сборки {Id}.", instance.Id);
        }
    }
}
