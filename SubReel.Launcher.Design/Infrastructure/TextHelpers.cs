namespace SubReel.Launcher.Design.Infrastructure;

public static class TextHelpers
{
    /// <summary>Ссылка из внешнего источника годится для показа/открытия, только если это http или https.</summary>
    public static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Русское склонение по числу: 1 версия, 2 версии, 5 версий.</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        if (mod100 is >= 11 and <= 14) return many;
        return mod10 == 1 ? one : mod10 is >= 2 and <= 4 ? few : many;
    }
}
