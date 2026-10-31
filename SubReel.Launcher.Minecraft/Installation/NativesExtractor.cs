using System.IO.Compression;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Games;
using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Minecraft.Layout;

namespace SubReel.Launcher.Minecraft.Installation;

/// <summary>Распаковывает нативные библиотеки старого формата (поле natives) в папку экземпляра. META-INF и записи из extract.exclude пропускаются.</summary>
public sealed class NativesExtractor : INativesExtractor
{
    private readonly GameFileLayout _layout;
    private readonly OsEnvironment _environment;
    private readonly ILogger<NativesExtractor> _logger;

    public NativesExtractor(GameFileLayout layout, OsEnvironment environment, ILogger<NativesExtractor> logger)
    {
        _layout = layout;
        _environment = environment;
        _logger = logger;
    }

    public Task<int> ExtractAsync(ResolvedVersion version, string targetDirectory, CancellationToken ct = default) =>
        Task.Run(() => Extract(version, targetDirectory, ct), ct);

    private int Extract(ResolvedVersion version, string targetDirectory, CancellationToken ct)
    {
        var target = Path.GetFullPath(targetDirectory);
        var prefix = target.EndsWith(Path.DirectorySeparatorChar) ? target : target + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(target);

        var count = 0;
        foreach (var library in version.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, _environment)) continue;

            var classifier = library.GetNativeClassifier(_environment);
            if (classifier is null || !library.Classifiers.TryGetValue(classifier, out var artifact)) continue;

            var jarPath = _layout.LibraryPath(artifact.Path);
            if (!File.Exists(jarPath)) throw new FileNotFoundException($"Нативная библиотека «{library.Name}» не установлена.", jarPath);

            using var zip = ZipFile.OpenRead(jarPath);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Name.Length == 0 || IsExcluded(entry.FullName, library.ExtractExclude)) continue;

                var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Архив «{library.Name}» содержит недопустимый путь «{entry.FullName}».");

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
                count++;
            }
        }

        _logger.LogInformation("Нативные библиотеки версии {Id} распакованы: {Count} файлов в {Directory}.", version.Id, count, target);
        return count;
    }

    private static bool IsExcluded(string entryName, IReadOnlyList<string> exclude) =>
        entryName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)
        || exclude.Any(prefix => entryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
