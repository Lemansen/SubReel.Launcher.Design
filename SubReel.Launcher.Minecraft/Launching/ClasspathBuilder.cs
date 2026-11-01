using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Minecraft.Layout;

namespace SubReel.Launcher.Minecraft.Launching;

/// <summary>
/// Classpath игры: основные файлы библиотек, разрешённые правилами для этой платформы, затем клиентский jar.
/// Архивы нативов старого формата (classifiers) в classpath не входят — их распаковывает <c>NativesExtractor</c>.
/// Новые нативы (отдельные библиотеки с правилами по ОС) входят, как и у официального лаунчера.
/// </summary>
public sealed class ClasspathBuilder
{
    private readonly GameFileLayout _layout;
    private readonly OsEnvironment _environment;

    public ClasspathBuilder(GameFileLayout layout, OsEnvironment environment)
    {
        _layout = layout;
        _environment = environment;
    }

    public IReadOnlyList<string> Build(ResolvedVersion version)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, _environment)) continue;
            if (library.Artifact is not { } artifact) continue;

            var path = _layout.LibraryPath(artifact.Path);
            if (seen.Add(path)) paths.Add(path);
        }

        var clientJar = _layout.ClientJarPath(version.ClientJarVersionId);
        if (seen.Add(clientJar)) paths.Add(clientJar);

        return paths;
    }
}
