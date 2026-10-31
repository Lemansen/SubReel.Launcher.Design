using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Domain.Java;

namespace SubReel.Launcher.Minecraft.Java;

/// <summary>
/// Читает версию установленной Java. Сначала файл <c>release</c> в корне установки (быстро, без запуска процесса);
/// если его нет — запускает <c>java -version</c> с ограничением по времени.
/// </summary>
public sealed partial class JavaVersionReader
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<JavaVersionReader> _logger;

    public JavaVersionReader(ILogger<JavaVersionReader> logger) => _logger = logger;

    [GeneratedRegex("version \"([^\"]+)\"")]
    private static partial Regex VersionInOutput();

    public static string ExecutablePath(string home) => Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");

    /// <summary>null — в папке нет работающей Java. Отмена пробрасывается.</summary>
    public async Task<JavaInstallation?> ReadAsync(string home, JavaSource source, string? component, CancellationToken ct)
    {
        var executable = ExecutablePath(home);
        if (!File.Exists(executable)) return null;

        var fromRelease = await ReadReleaseAsync(home, ct).ConfigureAwait(false);
        if (fromRelease is { } release)
            return new JavaInstallation(Path.GetFullPath(home), Path.GetFullPath(executable), release.Major, release.Version, release.Is64Bit, source, release.Vendor, component);

        var fromProcess = await RunVersionAsync(executable, ct).ConfigureAwait(false);
        if (fromProcess is not { } output) return null;

        return new JavaInstallation(Path.GetFullPath(home), Path.GetFullPath(executable), output.Major, output.Version, output.Is64Bit, source, null, component);
    }

    /// <summary>Разбор файла release: строки вида <c>JAVA_VERSION="21.0.4"</c>. null — версии в файле нет.</summary>
    internal static (int Major, string Version, bool Is64Bit, string? Vendor)? ParseRelease(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"');
        }

        if (!values.TryGetValue("JAVA_VERSION", out var version) || JavaVersionParser.ParseMajor(version) is not { } major) return null;

        values.TryGetValue("OS_ARCH", out var arch);
        var is64Bit = arch is null || arch.Contains("64", StringComparison.Ordinal) || arch.Equals("aarch64", StringComparison.OrdinalIgnoreCase);
        values.TryGetValue("IMPLEMENTOR", out var vendor);
        return (major, version, is64Bit, string.IsNullOrWhiteSpace(vendor) ? null : vendor);
    }

    /// <summary>Разбор вывода <c>java -version</c>: <c>openjdk version "21.0.4" …</c>, признак «64-Bit» в строке виртуальной машины.</summary>
    internal static (int Major, string Version, bool Is64Bit)? ParseVersionOutput(string output)
    {
        var match = VersionInOutput().Match(output);
        if (!match.Success) return null;

        var version = match.Groups[1].Value;
        return JavaVersionParser.ParseMajor(version) is { } major
            ? (major, version, output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private async Task<(int Major, string Version, bool Is64Bit, string? Vendor)?> ReadReleaseAsync(string home, CancellationToken ct)
    {
        var path = Path.Combine(home, "release");
        if (!File.Exists(path)) return null;

        try
        {
            return ParseRelease(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Файл release в {Home} не прочитан, версия будет определена запуском Java.", home);
            return null;
        }
    }

    private async Task<(int Major, string Version, bool Is64Bit)?> RunVersionAsync(string executable, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-version");

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogInformation(ex, "Не удалось запустить {Executable}.", executable);
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProcessTimeout);
        try
        {
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return ParseVersionOutput(await errors.ConfigureAwait(false) + "\n" + await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested) throw;
            _logger.LogWarning("{Executable} не ответила на -version за {Seconds} с.", executable, ProcessTimeout.TotalSeconds);
            return null;
        }
    }

    private void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _logger.LogDebug(ex, "Процесс Java уже завершён.");
        }
    }
}
