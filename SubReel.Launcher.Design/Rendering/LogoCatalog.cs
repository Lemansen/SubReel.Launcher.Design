using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SubReel.Launcher.Design.Rendering;

/// <summary>
/// Логотипы из ресурсов <c>Assets/Logos/&lt;имя&gt;.png</c>. Файла нет — возвращается null, и вызывающий рисует запасную иконку,
/// поэтому приложение работает и без логотипов. Результат (в том числе «нет») запоминается.
/// </summary>
public static class LogoCatalog
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Cache.GetOrAdd(name, Load);
    }

    public static string? NameFor(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => "fabric",
        LoaderKind.Quilt => "quilt",
        LoaderKind.Forge => "forge",
        LoaderKind.NeoForge => "neoforge",
        _ => null,
    };

    private static ImageSource? Load(string name)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/Logos/{name}.png", UriKind.Absolute);
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is null) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (IOException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (UriFormatException) { return null; }
    }
}
