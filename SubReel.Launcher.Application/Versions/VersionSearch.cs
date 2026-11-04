namespace SubReel.Launcher.Application.Versions;

/// <summary>Чистая логика выпадающего списка версий: поиск по нескольким полям и подпись «когда запускали». Без интерфейса — проверяется тестами.</summary>
public static class VersionSearch
{
    /// <summary>
    /// Строка запроса делится на слова; строка списка подходит, если каждое слово встречается хотя бы в одном из полей (без учёта регистра).
    /// «fabric 1.21» найдёт сборки Fabric для 1.21.x. Пустой запрос подходит всему.
    /// </summary>
    public static bool Matches(string? query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;

        var haystack = string.Join(' ', fields.Where(f => !string.IsNullOrEmpty(f)));
        foreach (var word in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (haystack.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Когда сборку запускали в последний раз, словами. Обе даты должны быть в одном часовом поясе (обычно местном):
    /// «вчера» считается по календарным дням, а не по 24 часам.
    /// </summary>
    public static string FormatLastPlayed(DateTime? lastPlayed, DateTime now)
    {
        if (lastPlayed is not { } last) return "ещё не запускалась";

        var span = now - last;
        if (span < TimeSpan.FromMinutes(1)) return "только что";
        if (last.Date == now.Date) return span < TimeSpan.FromHours(1) ? $"{(int)span.TotalMinutes} мин. назад" : "сегодня";

        var days = (now.Date - last.Date).Days;
        if (days == 1) return "вчера";
        if (days < 7) return $"{days} дн. назад";
        if (days < 30) return $"{days / 7} нед. назад";
        if (days < 365) return $"{days / 30} мес. назад";
        return "более года назад";
    }

    /// <summary>Наигранное время для строки списка: «не играл», «менее минуты», «25 мин», «3 ч 05 мин».</summary>
    public static string FormatPlayTime(long seconds)
    {
        if (seconds <= 0) return "не играл";
        if (seconds < 60) return "менее минуты";

        var totalMinutes = seconds / 60;
        if (totalMinutes < 60) return $"{totalMinutes} мин";
        return $"{totalMinutes / 60} ч {totalMinutes % 60:00} мин";
    }
}
