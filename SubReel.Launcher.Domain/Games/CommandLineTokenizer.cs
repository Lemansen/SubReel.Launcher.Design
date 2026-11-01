using System.Text;

namespace SubReel.Launcher.Domain.Games;

/// <summary>
/// Делит строку аргументов (поле «Аргументы JVM» экземпляра) на части по пробелам. Часть в двойных кавычках остаётся целой,
/// сами кавычки убираются: <c>-Dname="a b" -Xss2M</c> → <c>-Dname=a b</c>, <c>-Xss2M</c>.
/// </summary>
public static class CommandLineTokenizer
{
    public static IReadOnlyList<string> Split(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken) result.Add(current.ToString());
        return result;
    }
}
