namespace SubReel.Launcher.Domain.Games;

/// <summary>
/// Склеивает цепочку inheritsFrom в одну версию. Правила: скалярные поля (mainClass, assets, logging, Java) берутся у ближайшего
/// потомка, где они заданы; библиотеки потомка идут первыми, а одинаковые по ключу (без версии) из родителя отбрасываются;
/// аргументы родителя идут раньше аргументов потомка.
/// </summary>
public static class VersionResolver
{
    public static ResolvedVersion Resolve(string requestedId, IReadOnlyList<GameVersionMetadata> chainChildFirst)
    {
        if (chainChildFirst.Count == 0) throw new VersionMetadataException($"У версии «{requestedId}» нет описания.");

        var mainClass = chainChildFirst.Select(m => m.MainClass).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
            ?? throw new VersionMetadataException($"У версии «{requestedId}» не указан главный класс (mainClass).");

        var assetIndex = chainChildFirst.Select(m => m.AssetIndex).FirstOrDefault(a => a is not null);
        var assetsId = chainChildFirst.Select(m => m.Assets).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? assetIndex?.Id ?? "legacy";

        var clientOwner = chainChildFirst.FirstOrDefault(m => m.ClientDownload is not null);

        var libraries = new List<Library>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metadata in chainChildFirst)
        {
            foreach (var library in metadata.Libraries)
            {
                if (seen.Add(library.Key)) libraries.Add(library);
            }
        }

        var parentFirst = chainChildFirst.Reverse().ToList();

        return new ResolvedVersion
        {
            Id = requestedId,
            Chain = chainChildFirst.Select(m => m.Id).ToList(),
            MainClass = mainClass,
            AssetsId = assetsId,
            AssetIndex = assetIndex,
            ClientDownload = clientOwner?.ClientDownload,
            ClientJarVersionId = clientOwner?.Id ?? requestedId,
            Libraries = libraries,
            JvmArguments = parentFirst.SelectMany(m => m.JvmArguments).ToList(),
            GameArguments = parentFirst.SelectMany(m => m.GameArguments).ToList(),
            LegacyGameArguments = chainChildFirst.Select(m => m.LegacyGameArguments).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
            Logging = chainChildFirst.Select(m => m.Logging).FirstOrDefault(l => l is not null),
            JavaMajorVersion = chainChildFirst.Select(m => m.JavaMajorVersion).FirstOrDefault(v => v is not null),
            JavaComponent = chainChildFirst.Select(m => m.JavaComponent).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)),
            Type = chainChildFirst[0].Type,
        };
    }
}
