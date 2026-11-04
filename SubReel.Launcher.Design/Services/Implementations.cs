using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

namespace SubReel.Launcher.Design.Services;

public sealed class ImageService : IImageService
{
    private readonly IImageCache _cache;
    private readonly ILogger<ImageService> _logger;

    public ImageService(IImageCache cache, ILogger<ImageService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<ImageSource?> LoadAsync(string url, int decodePixelWidth, TimeSpan? maxAge = null)
    {
        var bytes = await _cache.GetBytesAsync(url, maxAge).ConfigureAwait(false);
        if (bytes is null) return null;

        var image = await Task.Run(() => ImageDecoder.Decode(bytes, decodePixelWidth)).ConfigureAwait(false);
        if (image is null)
        {
            // Битый файл в кэше (обрыв загрузки и т.п.): удаляем, чтобы при следующем запросе скачать заново.
            _logger.LogWarning("Картинка {Url} не декодируется, файл удалён из кэша.", url);
            _cache.Invalidate(url);
        }
        return image;
    }

    public Task PrefetchAsync(IEnumerable<string> urls, CancellationToken ct = default) => _cache.PrefetchAsync(urls, ct);
}

public sealed class DialogService : IDialogService
{
    public string? PickImageFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбери картинку для аватарки",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Все файлы|*.*",
        };
        var owner = System.Windows.Application.Current?.MainWindow;
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FileName : null;
    }

    public string? PickModpackFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбери файл сборки",
            Filter = "Сборки (*.mrpack, *.zip)|*.mrpack;*.zip|Сборки Modrinth (*.mrpack)|*.mrpack|Сборки CurseForge (*.zip)|*.zip|Все файлы|*.*",
        };
        var owner = System.Windows.Application.Current?.MainWindow;
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        var owner = System.Windows.Application.Current?.MainWindow;
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FolderName : null;
    }
}

public sealed class AppShutdown : IAppShutdown
{
    public void Shutdown() => System.Windows.Application.Current?.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
}

public sealed class BrowserService : IBrowserService
{
    private readonly ILogger<BrowserService> _logger;

    public BrowserService(ILogger<BrowserService> logger) => _logger = logger;

    public bool TryOpen(string? url)
    {
        // Ссылка приходит из внешнего источника: открываем только http/https.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "Не удалось открыть ссылку {Url} во внешнем браузере.", uri);
            return false;
        }
    }
}

public sealed class ClipboardService : IClipboardService
{
    private readonly ILogger<ClipboardService> _logger;

    public ClipboardService(ILogger<ClipboardService> logger) => _logger = logger;

    public bool TrySetText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            // Буфер обмена может быть занят другой программой — это штатная ситуация.
            _logger.LogWarning(ex, "Не удалось записать текст в буфер обмена.");
            return false;
        }
    }
}
