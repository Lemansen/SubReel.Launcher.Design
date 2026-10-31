using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace SubReel.Launcher.Design.Services;

/// <summary>Превращает байты картинки в замороженный <see cref="BitmapImage"/>. Работает в любом потоке.</summary>
public static class ImageDecoder
{
    /// <summary>Декодирует не шире, чем нужно, но и не растягивает маленькие картинки (это только съедает память). null — файл не является картинкой.</summary>
    public static BitmapImage? Decode(byte[] bytes, int decodePixelWidth)
    {
        try
        {
            int naturalWidth;
            using (var probe = new MemoryStream(bytes))
                naturalWidth = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelWidth;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (naturalWidth > decodePixelWidth) bmp.DecodePixelWidth = decodePixelWidth;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or IOException or COMException)
        {
            return null; // битая или неподдерживаемая картинка: вызывающий код показывает заглушку
        }
    }

    /// <summary>Картинка с диска (своя аватарка). Файл читается целиком и не блокируется. null — файла нет или он не читается как картинка.</summary>
    public static async Task<BitmapImage?> LoadFileAsync(string? path, int decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            return await Task.Run(() => Decode(bytes, decodePixelWidth)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
