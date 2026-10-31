using System.Text.RegularExpressions;

namespace SubReel.Launcher.Domain.Games;

/// <summary>Правило из version.json: разрешает или запрещает библиотеку или аргумент для платформы и набора возможностей.</summary>
public sealed record Rule(bool Allow, string? OsName = null, string? OsArch = null, string? OsVersionPattern = null, IReadOnlyDictionary<string, bool>? Features = null);

public static class RuleEvaluator
{
    /// <summary>
    /// Без правил — разрешено. Иначе начинаем с «запрещено», а каждое подошедшее правило по порядку переопределяет результат
    /// (побеждает последнее подошедшее) — так читает правила официальный лаунчер.
    /// </summary>
    public static bool IsAllowed(IReadOnlyList<Rule> rules, OsEnvironment environment)
    {
        if (rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (Matches(rule, environment)) allowed = rule.Allow;
        }
        return allowed;
    }

    private static bool Matches(Rule rule, OsEnvironment env)
    {
        if (rule.OsName is not null && !string.Equals(rule.OsName, env.OsName, StringComparison.OrdinalIgnoreCase)) return false;
        if (rule.OsArch is not null && !string.Equals(rule.OsArch, env.Arch, StringComparison.OrdinalIgnoreCase)) return false;
        if (rule.OsVersionPattern is not null && !VersionMatches(rule.OsVersionPattern, env.OsVersion)) return false;

        if (rule.Features is not null)
        {
            foreach (var (name, required) in rule.Features)
            {
                var actual = env.Features is not null && env.Features.TryGetValue(name, out var value) && value;
                if (actual != required) return false;
            }
        }
        return true;
    }

    private static bool VersionMatches(string pattern, string? version)
    {
        if (version is null) return false;
        try
        {
            return Regex.IsMatch(version, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false; // непонятное правило не должно разрешать библиотеку
        }
    }
}
