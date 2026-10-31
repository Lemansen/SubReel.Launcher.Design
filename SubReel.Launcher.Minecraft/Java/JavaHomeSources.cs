using System.Runtime.Versioning;
using SubReel.Launcher.Domain.Java;

namespace SubReel.Launcher.Minecraft.Java;

/// <summary>Источник папок, в которых может лежать Java. Только перечисляет кандидатов: версию проверяет <see cref="JavaVersionReader"/>.</summary>
public interface IJavaHomeSource
{
    JavaSource Source { get; }

    IEnumerable<string> GetHomes();
}

/// <summary>Переменная JAVA_HOME и папки из PATH, где лежит java.</summary>
public sealed class JavaHomeEnvironmentSource : IJavaHomeSource
{
    private readonly Func<string, string?> _getVariable;

    public JavaHomeEnvironmentSource() : this(Environment.GetEnvironmentVariable) { }

    internal JavaHomeEnvironmentSource(Func<string, string?> getVariable) => _getVariable = getVariable;

    public JavaSource Source => JavaSource.JavaHome;

    public IEnumerable<string> GetHomes()
    {
        if (_getVariable("JAVA_HOME") is { Length: > 0 } javaHome) yield return javaHome.Trim().Trim('"');

        foreach (var entry in (_getVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length == 0 || !File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? "java.exe" : "java"))) continue;

            var parent = Directory.GetParent(directory);
            if (parent is not null && string.Equals(Path.GetFileName(directory), "bin", StringComparison.OrdinalIgnoreCase)) yield return parent.FullName;
        }
    }
}

/// <summary>Типовые папки установки JDK и JRE разных поставщиков.</summary>
public sealed class KnownFoldersJavaSource : IJavaHomeSource
{
    private readonly IReadOnlyList<string> _roots;

    public KnownFoldersJavaSource() : this(DefaultRoots()) { }

    internal KnownFoldersJavaSource(IReadOnlyList<string> roots) => _roots = roots;

    public JavaSource Source => JavaSource.KnownFolder;

    public IEnumerable<string> GetHomes()
    {
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(root).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // папка недоступна — это не ошибка поиска
            }

            foreach (var child in children)
            {
                yield return child;
                yield return Path.Combine(child, "Contents", "Home"); // макет macOS: *.jdk/Contents/Home
            }
        }
    }

    private static IReadOnlyList<string> DefaultRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            var folders = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 };
            var vendors = new[] { "Java", "Eclipse Adoptium", "Eclipse Foundation", "AdoptOpenJDK", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto", "Semeru", "Temurin" };
            return folders.Select(Environment.GetFolderPath).Where(f => f.Length > 0).SelectMany(f => vendors.Select(v => Path.Combine(f, v))).ToList();
        }

        return OperatingSystem.IsMacOS()
            ? new[] { "/Library/Java/JavaVirtualMachines" }
            : new[] { "/usr/lib/jvm", "/usr/java", "/opt/java", "/opt/jdk" };
    }
}

/// <summary>Рантаймы официального лаунчера Minecraft: <c>.minecraft/runtime/&lt;компонент&gt;/&lt;платформа&gt;/&lt;компонент&gt;</c>.</summary>
public sealed class OfficialLauncherJavaSource : IJavaHomeSource
{
    private readonly string _runtimeRoot;

    public OfficialLauncherJavaSource() : this(DefaultRoot()) { }

    internal OfficialLauncherJavaSource(string runtimeRoot) => _runtimeRoot = runtimeRoot;

    public JavaSource Source => JavaSource.OfficialLauncher;

    public IEnumerable<string> GetHomes()
    {
        if (!Directory.Exists(_runtimeRoot)) yield break;

        List<string> homes = new();
        try
        {
            foreach (var component in Directory.EnumerateDirectories(_runtimeRoot))
            foreach (var platform in Directory.EnumerateDirectories(component))
                homes.AddRange(Directory.EnumerateDirectories(platform));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Что успели собрать — отдаём; остальное официальный лаунчер мог удалить прямо во время обхода.
        }

        foreach (var home in homes) yield return home;
    }

    private static string DefaultRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (OperatingSystem.IsWindows()) return Path.Combine(appData, ".minecraft", "runtime");
        if (OperatingSystem.IsMacOS()) return Path.Combine(appData, "minecraft", "runtime");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft", "runtime");
    }
}

/// <summary>Реестр Windows: JavaSoft и ключи популярных поставщиков (Adoptium, Microsoft, Zulu …), 64- и 32-битные представления.</summary>
public sealed class WindowsRegistryJavaSource : IJavaHomeSource
{
    private static readonly string[] BaseKeys =
    {
        @"SOFTWARE\JavaSoft",
        @"SOFTWARE\Eclipse Adoptium",
        @"SOFTWARE\Eclipse Foundation",
        @"SOFTWARE\AdoptOpenJDK",
        @"SOFTWARE\Microsoft\JDK",
        @"SOFTWARE\Azul Systems\Zulu",
        @"SOFTWARE\BellSoft",
        @"SOFTWARE\Amazon Corretto",
    };

    private static readonly string[] ValueNames = { "JavaHome", "Path", "InstallationPath" };
    private const int MaxDepth = 4;

    public JavaSource Source => JavaSource.Registry;

    public IEnumerable<string> GetHomes() => OperatingSystem.IsWindows() ? ReadHomes() : Array.Empty<string>();

    [SupportedOSPlatform("windows")]
    private static List<string> ReadHomes()
    {
        var homes = new List<string>();
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            try
            {
                using var machine = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
                foreach (var baseKey in BaseKeys)
                {
                    using var key = machine.OpenSubKey(baseKey);
                    if (key is not null) Collect(key, 0, homes);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Нет доступа к ветке реестра — остальные источники продолжают работать.
            }
        }
        return homes;
    }

    [SupportedOSPlatform("windows")]
    private static void Collect(Microsoft.Win32.RegistryKey key, int depth, List<string> homes)
    {
        foreach (var name in ValueNames)
        {
            if (key.GetValue(name) is string value && value.Length > 0) homes.Add(value.Trim().Trim('"'));
        }

        if (depth >= MaxDepth) return;
        foreach (var childName in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(childName);
            if (child is not null) Collect(child, depth + 1, homes);
        }
    }
}
