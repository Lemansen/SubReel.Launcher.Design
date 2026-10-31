using System.Text.RegularExpressions;

namespace SubReel.Launcher.Domain.Java;

/// <summary>Разбор строк версии Java: «1.8.0_392», «17.0.9», «21», «9-ea».</summary>
public static partial class JavaVersionParser
{
    [GeneratedRegex(@"^\s*(\d+)(?:\.(\d+))?")]
    private static partial Regex LeadingNumbers();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();

    /// <summary>Мажорная версия: у Java 8 и старше она записана как «1.x», у новых — первым числом. null — строка не похожа на версию.</summary>
    public static int? ParseMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        var match = LeadingNumbers().Match(version);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var first)) return null;

        if (first == 1 && match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var second)) return second;
        return first;
    }

    /// <summary>Сравнение полных версий по числовым частям: 21.0.10 новее 21.0.9. Нечисловые хвосты игнорируются.</summary>
    public static int Compare(string? left, string? right)
    {
        var a = Parts(left);
        var b = Parts(right);
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static List<long> Parts(string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? new List<long>()
            : Numbers().Matches(version).Select(m => long.TryParse(m.Value, out var n) ? n : 0).ToList();
}
