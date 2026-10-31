using System.Text.Json;
using System.Text.RegularExpressions;
using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Infrastructure.Versions;

/// <summary>Определяет по папке <c>versions\&lt;id&gt;</c>, что за версия установлена: ваниль, Fabric, Forge, NeoForge, Quilt или другая сборка.</summary>
public static class InstalledVersionParser
{
    private static readonly Regex FabricQuiltLoader = new(@"^(?:fabric|quilt)-loader-(?<v>\d[\w.]*?)-(?=\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NeoForgeVersion = new(@"neoforge-?(?<v>\d[\w.+\-]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ForgeVersion = new(@"forge-?(?:\d+(?:\.\d+)+-)?(?<v>\d+(?:\.\d+)+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LeadingMc = new(@"^\d+\.\d+(?:\.\d+)?", RegexOptions.Compiled);

    /// <summary>Читает JSON версии и определяет загрузчик. null — не версия (или ваниль без клиентского jar).</summary>
    public static InstalledVersion? Parse(string id, string dir, string jsonPath)
    {
        using var stream = File.OpenRead(jsonPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var inherits = Str(root, "inheritsFrom");
        var mainClass = Str(root, "mainClass") ?? "";
        var lower = id.ToLowerInvariant();

        LoaderKind kind;
        string? loaderVersion = null;

        if (lower.StartsWith("fabric-loader-") || mainClass.Contains("net.fabricmc", StringComparison.OrdinalIgnoreCase))
        {
            kind = LoaderKind.Fabric;
            loaderVersion = FabricQuiltLoader.Match(id) is { Success: true } m ? m.Groups["v"].Value : null;
        }
        else if (lower.StartsWith("quilt-loader-") || mainClass.Contains("org.quiltmc", StringComparison.OrdinalIgnoreCase))
        {
            kind = LoaderKind.Quilt;
            loaderVersion = FabricQuiltLoader.Match(id) is { Success: true } m ? m.Groups["v"].Value : null;
        }
        else if (lower.Contains("neoforge"))
        {
            kind = LoaderKind.NeoForge;
            loaderVersion = NeoForgeVersion.Match(id) is { Success: true } m ? m.Groups["v"].Value : null;
        }
        else if (lower.Contains("forge"))
        {
            kind = LoaderKind.Forge;
            loaderVersion = ForgeVersion.Match(id) is { Success: true } m ? m.Groups["v"].Value : null;
        }
        else if (inherits is not null)
        {
            kind = LoaderKind.Other; // OptiFine, LabyMod и т.п.
        }
        else
        {
            // Ваниль считается установленной только вместе с клиентским jar.
            if (!File.Exists(Path.Combine(dir, id + ".jar"))) return null;
            kind = LoaderKind.Vanilla;
        }

        var mc = inherits ?? (LeadingMc.Match(id) is { Success: true } lead ? lead.Value : id);
        return new InstalledVersion { Id = id, McVersion = mc, Loader = kind, LoaderVersion = loaderVersion };
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Секции по типу загрузчика, внутри — новые версии Minecraft первыми.</summary>
    public static int Compare(InstalledVersion a, InstalledVersion b)
    {
        var byLoader = a.Loader.CompareTo(b.Loader);
        if (byLoader != 0) return byLoader;
        var byMc = CompareNumeric(b.McVersion, a.McVersion);
        if (byMc != 0) return byMc;
        var byLoaderVersion = CompareNumeric(b.LoaderVersion ?? "", a.LoaderVersion ?? "");
        return byLoaderVersion != 0 ? byLoaderVersion : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Сравнение «по-человечески»: 1.9 &lt; 1.10, 21.1.9 &lt; 21.1.50. Нечисловые куски сравниваются как строки.</summary>
    private static int CompareNumeric(string x, string y)
    {
        var px = x.Split('.', '-', '+');
        var py = y.Split('.', '-', '+');
        for (var i = 0; i < Math.Max(px.Length, py.Length); i++)
        {
            var sx = i < px.Length ? px[i] : "";
            var sy = i < py.Length ? py[i] : "";
            var cmp = long.TryParse(sx, out var nx) && long.TryParse(sy, out var ny)
                ? nx.CompareTo(ny)
                : string.Compare(sx, sy, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
        }
        return 0;
    }
}
