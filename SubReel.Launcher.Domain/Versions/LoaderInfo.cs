using System.Text.RegularExpressions;

namespace SubReel.Launcher.Domain.Versions;

/// <summary>Как загрузчик называется и какой монограммой подписывается. Цвета — забота представления (см. LoaderAccent в приложении).</summary>
public static class LoaderInfo
{
    public static string Name(LoaderKind kind) => kind switch
    {
        LoaderKind.Vanilla => "Vanilla",
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        LoaderKind.Quilt => "Quilt",
        _ => "Другие загрузчики",
    };

    public static string Monogram(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => "Fa",
        LoaderKind.Forge => "Fo",
        LoaderKind.NeoForge => "NF",
        LoaderKind.Quilt => "Q",
        _ => "•",
    };

    /// <summary>
    /// Загрузчик, который лаунчер умеет ставить сам: профиль берётся с мета-сервера и ставится как обычная версия с <c>inheritsFrom</c> (Fabric, Quilt).
    /// Forge и NeoForge ставятся установщиком с обработчиками — это отдельный шаг фазы 10.
    /// </summary>
    public static bool CanInstall(LoaderKind kind) => kind is LoaderKind.Fabric or LoaderKind.Quilt;

    // fabric-loader-0.16.5-1.21.1, quilt-loader-0.27.0-beta.1-1.21.1, fabric-loader-0.15.11-24w14a
    private static readonly Regex ProfileIdPattern = new(
        @"^(?<kind>fabric|quilt)-loader-(?<v>\d[\w.]*(?:-[A-Za-z][\w.]*)?)-(?<mc>\d[\w.\-]*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Имя папки профиля загрузчика в <c>versions</c> — так его называют мета-серверы Fabric и Quilt (то же имя у профилей,
    /// установленных официальным лаунчером, поэтому сканер папки <c>.minecraft</c> узнаёт их по тому же правилу).
    /// </summary>
    public static string ProfileId(LoaderKind kind, string mcVersion, string loaderVersion) => kind switch
    {
        LoaderKind.Fabric => $"fabric-loader-{loaderVersion}-{mcVersion}",
        LoaderKind.Quilt => $"quilt-loader-{loaderVersion}-{mcVersion}",
        _ => throw new NotSupportedException($"Профиль загрузчика {Name(kind)} не строится по имени: его создаёт установщик."),
    };

    /// <summary>Обратное к <see cref="ProfileId"/>. null — имя не похоже на профиль Fabric или Quilt.</summary>
    public static LoaderProfileRef? TryParseProfileId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var match = ProfileIdPattern.Match(id);
        if (!match.Success) return null;

        var kind = string.Equals(match.Groups["kind"].Value, "quilt", StringComparison.OrdinalIgnoreCase) ? LoaderKind.Quilt : LoaderKind.Fabric;
        return new LoaderProfileRef(kind, match.Groups["v"].Value, match.Groups["mc"].Value);
    }
}

/// <summary>Сборка загрузчика, которую можно поставить на версию Minecraft (по данным мета-сервера загрузчика).</summary>
/// <param name="Stable">false — бета или предварительная сборка.</param>
public sealed record LoaderBuild(LoaderKind Kind, string McVersion, string Version, bool Stable)
{
    /// <summary>Имя папки профиля в <c>versions</c> общего кэша игры.</summary>
    public string ProfileId => LoaderInfo.ProfileId(Kind, McVersion, Version);
}

/// <summary>Что записано в имени профиля загрузчика: загрузчик, его версия и версия игры.</summary>
public sealed record LoaderProfileRef(LoaderKind Kind, string LoaderVersion, string McVersion);
