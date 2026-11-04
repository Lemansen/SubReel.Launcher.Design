using SubReel.Launcher.Application.Versions;

namespace SubReel.Launcher.Tests;

public sealed class VersionSearchTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_query_matches_everything(string? query) => Assert.True(VersionSearch.Matches(query, "Моя сборка"));

    [Theory]
    [InlineData("fabric", true)]
    [InlineData("FABRIC", true)]
    [InlineData("1.21", true)]
    [InlineData("fabric 1.21", true)]
    [InlineData("1.21 fabric", true)]
    [InlineData("сборк", true)]
    [InlineData("forge", false)]
    [InlineData("fabric 1.20", false)]
    public void Every_word_must_be_found_in_some_field(string query, bool expected) =>
        Assert.Equal(expected, VersionSearch.Matches(query, "Моя сборка", "Minecraft 1.21.1", "Fabric", "0.16.0"));

    [Fact]
    public void Missing_fields_are_ignored() => Assert.True(VersionSearch.Matches("a", null, "", "abc"));

    private static readonly DateTime Now = new(2026, 11, 3, 15, 0, 0);

    [Fact]
    public void Never_played_is_stated_plainly() => Assert.Equal("ещё не запускалась", VersionSearch.FormatLastPlayed(null, Now));

    [Theory]
    [InlineData(0, "только что")]       // 15:00
    [InlineData(-30, "только что")]     // будущее (сбой часов) не должно давать отрицательных чисел
    public void Very_recent_or_future_is_just_now(int minutesAgo, string expected) =>
        Assert.Equal(expected, VersionSearch.FormatLastPlayed(Now.AddMinutes(-minutesAgo), Now));

    [Fact]
    public void Minutes_hours_and_days_are_worded_by_calendar_day()
    {
        Assert.Equal("5 мин. назад", VersionSearch.FormatLastPlayed(Now.AddMinutes(-5), Now));
        Assert.Equal("сегодня", VersionSearch.FormatLastPlayed(Now.AddHours(-3), Now));
        Assert.Equal("вчера", VersionSearch.FormatLastPlayed(new DateTime(2026, 11, 2, 23, 50, 0), Now)); // 15 часов назад, но календарно вчера
        Assert.Equal("3 дн. назад", VersionSearch.FormatLastPlayed(Now.AddDays(-3), Now));
        Assert.Equal("2 нед. назад", VersionSearch.FormatLastPlayed(Now.AddDays(-15), Now));
        Assert.Equal("3 мес. назад", VersionSearch.FormatLastPlayed(Now.AddDays(-100), Now));
        Assert.Equal("более года назад", VersionSearch.FormatLastPlayed(Now.AddDays(-400), Now));
    }

    [Theory]
    [InlineData(0, "не играл")]
    [InlineData(-5, "не играл")]
    [InlineData(30, "менее минуты")]
    [InlineData(60, "1 мин")]
    [InlineData(25 * 60 + 59, "25 мин")]
    [InlineData(3600, "1 ч 00 мин")]
    [InlineData(3 * 3600 + 5 * 60, "3 ч 05 мин")]
    [InlineData(125 * 3600 + 30 * 60, "125 ч 30 мин")]
    public void FormatPlayTime_is_readable(long seconds, string expected) =>
        Assert.Equal(expected, VersionSearch.FormatPlayTime(seconds));
}
