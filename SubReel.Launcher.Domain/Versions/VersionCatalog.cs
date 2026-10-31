using System.Text.RegularExpressions;

namespace SubReel.Launcher.Domain.Versions;

/// <summary>Один релиз ванильного Minecraft Java.</summary>
public sealed class McVersion
{
    public required string Id { get; init; }
    public DateTimeOffset ReleaseTime { get; init; }
    /// <summary>Картинка обновления из патчноутов Mojang (если есть).</summary>
    public string? ImageUrl { get; set; }
    /// <summary>Короткое описание обновления из патчноутов (shortText), если есть.</summary>
    public string? Summary { get; set; }
}

/// <summary>
/// Карточка в режиме «Все версии». Обычно это линейка релизов с общим префиксом (1.21 → 1.21, 1.21.1 … 1.21.x),
/// но бывает и сводная карточка «Старые версии» (IsLegacy), в которую слиты старые линейки без картинок.
/// </summary>
public sealed class VersionGroup
{
    public required string Key { get; init; }
    /// <summary>Новые версии первыми.</summary>
    public required List<McVersion> Versions { get; init; }
    public string? ImageUrl { get; set; }
    public string? Summary { get; set; }
    /// <summary>Название обновления («Tricky Trials»), если известно. У сводной карточки — диапазон («1.0 – 1.6»).</summary>
    public string? Title { get; set; }
    /// <summary>Только у сводной карточки: линейки, которые в неё вошли (новые первыми).</summary>
    public List<VersionGroup>? Lines { get; init; }

    public bool IsLegacy => Lines is not null;
    public DateTimeOffset NewestRelease => Versions[0].ReleaseTime;
    public DateTimeOffset FirstRelease => Versions[^1].ReleaseTime;
}

/// <summary>
/// Каталог релизов Minecraft Java: список версий, линейки (1.21, 1.20 …) и карточки режима «Все версии».
/// Модель не знает, откуда пришли данные: загрузкой с серверов Mojang занимается слой Infrastructure.
/// </summary>
public sealed class VersionCatalog
{
    // Версии вида 1.21.4 / 26.1 / 26.1.2 → ключ группы «1.21» / «26.1».
    private static readonly Regex GroupRegex = new(@"^(\d+)\.(\d+)(?:\.\d+)?$", RegexOptions.Compiled);

    // Сводная карточка «Старые версии» создаётся, только если в хвосте списка не меньше стольких линеек без картинок.
    private const int LegacyMinLines = 3;

    // Названия больших обновлений, как на странице обновлений minecraft.net.
    private static readonly Dictionary<string, string> KnownLineTitles = new()
    {
        ["1.7"] = "The Update that Changed the World",
        ["1.8"] = "Bountiful Update",
        ["1.9"] = "Combat Update",
        ["1.10"] = "Frostburn Update",
        ["1.11"] = "Exploration Update",
        ["1.12"] = "World of Color Update",
        ["1.13"] = "Update Aquatic",
        ["1.14"] = "Village & Pillage",
        ["1.15"] = "Buzzy Bees",
        ["1.16"] = "Nether Update",
        ["1.17"] = "Caves & Cliffs: Part I",
        ["1.18"] = "Caves & Cliffs: Part II",
        ["1.19"] = "The Wild Update",
        ["1.20"] = "Trails & Tales",
        ["1.21"] = "Tricky Trials",
    };

    // Обновления нового формата (26.x) называются по месяцу первого релиза линейки.
    private static readonly Dictionary<(int Year, int Month), string> KnownDropTitles = new()
    {
        [(2026, 6)] = "Chaos Cubed",
        [(2026, 9)] = "Wilderness Bound",
    };

    private VersionCatalog()
    {
    }

    public IReadOnlyList<McVersion> Releases { get; private init; } = Array.Empty<McVersion>();
    /// <summary>Все линейки релизов (1.21, 1.20, …) — без слияния. Нужны для картинок, описаний и заголовков.</summary>
    public IReadOnlyList<VersionGroup> Lines { get; private init; } = Array.Empty<VersionGroup>();
    /// <summary>Карточки в режиме «Все версии»: линейки, где старые без картинок слиты в «Старые версии».</summary>
    public IReadOnlyList<VersionGroup> Groups { get; private init; } = Array.Empty<VersionGroup>();
    public string LatestRelease { get; private init; } = "";
    /// <summary>true — не удалось связаться с Mojang, показан встроенный список без картинок.</summary>
    public bool IsOffline { get; private init; }
    /// <summary>true — каталог настоящий, но взят из копии на диске (сети нет). Даты и картинки на месте; при появлении сети его стоит обновить.</summary>
    public bool IsStale { get; private init; }

    /// <summary>Тот же каталог, помеченный как загруженный из сохранённой копии.</summary>
    public VersionCatalog AsStale() => new() { Releases = Releases, Lines = Lines, Groups = Groups, LatestRelease = LatestRelease, IsStale = true };

    /// <summary>Подходит ли идентификатор под релиз формата «1.21.4» / «26.1».</summary>
    public static bool IsReleaseId(string? id) => id is not null && GroupRegex.IsMatch(id);

    /// <summary>Собирает каталог из списка релизов. Если latestRelease не найден среди релизов, берётся самый новый.</summary>
    public static VersionCatalog Create(IEnumerable<McVersion> releases, string? latestRelease)
    {
        var ordered = releases.OrderByDescending(v => v.ReleaseTime).ToList();
        var latest = latestRelease ?? "";
        if (ordered.Count > 0 && ordered.All(v => v.Id != latest)) latest = ordered[0].Id;

        var lines = BuildLines(ordered);
        return new VersionCatalog { Releases = ordered, Lines = lines, Groups = MergeLegacy(lines), LatestRelease = latest };
    }

    public McVersion? Find(string id) => Releases.FirstOrDefault(v => v.Id == id);

    /// <summary>Линейка версии: 1.21.4 → «1.21».</summary>
    public VersionGroup? LineOf(string id) => Lines.FirstOrDefault(g => g.Versions.Any(v => v.Id == id));

    /// <summary>Карточка, на которой лежит версия (для старых версий — «Старые версии»).</summary>
    public VersionGroup? CardOf(string id) => Groups.FirstOrDefault(g => g.Versions.Any(v => v.Id == id));

    /// <summary>Картинка для конкретной версии: своя, иначе картинка её линейки.</summary>
    public string? ImageFor(string id) => Find(id)?.ImageUrl ?? LineOf(id)?.ImageUrl;

    /// <summary>Короткое описание: своё у версии, иначе у её линейки.</summary>
    public string? SummaryFor(string id) => Find(id)?.Summary ?? LineOf(id)?.Summary;

    public static VersionCatalog CreateFallback()
    {
        // Встроенный список на случай отсутствия сети (порядок — от новых к старым).
        string[] ids =
        {
            "1.21.4", "1.21.3", "1.21.1", "1.21", "1.20.6", "1.20.4", "1.20.2", "1.20.1", "1.20",
            "1.19.4", "1.19.2", "1.19", "1.18.2", "1.18", "1.17.1", "1.16.5", "1.15.2", "1.14.4",
            "1.13.2", "1.12.2", "1.8.9", "1.7.10",
        };
        var start = new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var releases = ids.Select((id, i) => new McVersion { Id = id, ReleaseTime = start.AddDays(-30 * i) }).ToList();
        var lines = BuildLines(releases, withTitles: false); // даты выдуманные — названия по датам подбирать нельзя
        return new VersionCatalog { Releases = releases, Lines = lines, Groups = lines, LatestRelease = ids[0], IsOffline = true };
    }

    private static List<VersionGroup> BuildLines(List<McVersion> releases, bool withTitles = true) =>
        releases
            .GroupBy(v => { var m = GroupRegex.Match(v.Id); return $"{m.Groups[1].Value}.{m.Groups[2].Value}"; })
            .Select(g =>
            {
                var versions = g.OrderByDescending(v => v.ReleaseTime).ToList();
                // Предпочитаем картинку базовой версии линейки («1.21»), затем самой старой из имеющих картинку.
                var image = versions.FirstOrDefault(v => v.Id == g.Key)?.ImageUrl
                            ?? versions.AsEnumerable().Reverse().Select(v => v.ImageUrl).FirstOrDefault(u => u != null);
                var summary = versions.FirstOrDefault(v => v.Id == g.Key)?.Summary
                              ?? versions.AsEnumerable().Reverse().Select(v => v.Summary).FirstOrDefault(t => t != null);
                var line = new VersionGroup { Key = g.Key, Versions = versions, ImageUrl = image, Summary = summary };
                if (withTitles) line.Title = TitleFor(line);
                return line;
            })
            .OrderByDescending(g => g.NewestRelease)
            .ToList();

    private static string? TitleFor(VersionGroup line)
    {
        if (KnownLineTitles.TryGetValue(line.Key, out var title)) return title;
        var first = line.FirstRelease;
        return KnownDropTitles.TryGetValue((first.Year, first.Month), out var drop) ? drop : null;
    }

    /// <summary>
    /// Старые линейки без картинок (1.0, 1.1, 1.2 …) не нужны отдельными пустыми карточками: сливаем хвост списка
    /// в одну карточку «Старые версии». Если у других линеек картинок нет вообще (сбой патчноутов) — ничего не сливаем.
    /// </summary>
    private static List<VersionGroup> MergeLegacy(List<VersionGroup> lines)
    {
        var firstLegacy = lines.Count;
        while (firstLegacy > 0 && lines[firstLegacy - 1].ImageUrl is null) firstLegacy--;
        if (firstLegacy == 0 || lines.Count - firstLegacy < LegacyMinLines) return lines;

        var old = lines.Skip(firstLegacy).ToList(); // новые первыми
        var versions = old.SelectMany(l => l.Versions).OrderByDescending(v => v.ReleaseTime).ToList();
        var legacy = new VersionGroup
        {
            Key = "legacy",
            Versions = versions,
            Lines = old,
            Title = $"{old[^1].Key} – {old[0].Key}",
        };
        return lines.Take(firstLegacy).Append(legacy).ToList();
    }
}
