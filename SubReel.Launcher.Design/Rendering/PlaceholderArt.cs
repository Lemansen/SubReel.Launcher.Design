using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SubReel.Launcher.Design.Rendering;

/// <summary>
/// Пиксельная заглушка вместо картинки обновления: градиент из «блоков» с лёгким шумом и редкими светлыми вкраплениями.
/// Рисуется локально, поэтому карточки и фон выглядят прилично и без сети, и для старых версий без картинки.
/// Узор зависит только от ключа (например, «1.8»), поэтому для одной версии всегда одинаковый.
/// </summary>
public static class PlaceholderArt
{
    private static readonly (string From, string To)[] Palettes =
    {
        ("#1E3A8A", "#0F172A"), ("#115E59", "#0B1A1F"), ("#6D28D9", "#160F2E"),
        ("#9A3412", "#1F120B"), ("#0E7490", "#0B1620"), ("#166534", "#0C1A12"),
    };

    private static readonly Dictionary<string, BitmapSource> Cache = new();

    /// <summary>Для карточки 190×140.</summary>
    public static BitmapSource Card(string key) => Create(key, 19, 14, 10);

    /// <summary>Для фона главной (окно 1100×700).</summary>
    public static BitmapSource Hero(string key) => Create(key, 44, 28, 25);

    private static BitmapSource Create(string key, int cols, int rows, int scale)
    {
        var id = $"{key}|{cols}x{rows}";
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var cached)) return cached;
        }

        var seed = StableHash(key);
        var rng = new Random(seed);
        var palette = Palettes[(int)((uint)seed % (uint)Palettes.Length)];
        var a = (Color)ColorConverter.ConvertFromString(palette.From);
        var b = (Color)ColorConverter.ConvertFromString(palette.To);

        var width = cols * scale;
        var height = rows * scale;
        var stride = width * 4;
        var pixels = new byte[height * stride];

        for (var cy = 0; cy < rows; cy++)
        {
            for (var cx = 0; cx < cols; cx++)
            {
                var t = (cx / (double)(cols - 1) + cy / (double)(rows - 1)) / 2; // диагональный градиент
                var noise = (rng.NextDouble() - 0.5) * 0.28;
                var speck = rng.NextDouble() < 0.035 ? 0.35 : 0;                 // «руда»
                var k = noise + speck;

                var r = Channel(a.R, b.R, t, k);
                var g = Channel(a.G, b.G, t, k);
                var bl = Channel(a.B, b.B, t, k);

                for (var py = 0; py < scale; py++)
                {
                    var offset = (cy * scale + py) * stride + cx * scale * 4;
                    for (var px = 0; px < scale; px++)
                    {
                        pixels[offset++] = bl;
                        pixels[offset++] = g;
                        pixels[offset++] = r;
                        pixels[offset++] = 255;
                    }
                }
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        lock (Cache) Cache[id] = bitmap;
        return bitmap;
    }

    private static byte Channel(byte start, byte end, double t, double k) =>
        (byte)Math.Clamp(start + (end - start) * t + k * 70, 0, 255);

    private static int StableHash(string text)
    {
        var hash = 17;
        foreach (var ch in text) hash = unchecked(hash * 31 + ch);
        return hash & 0x7FFFFFFF;
    }
}
