using System.Windows.Media;

namespace SubReel.Launcher.Design.Services;

/// <summary>Загрузка и кэширование картинок. Возвращает уже готовые к показу (замороженные) изображения.</summary>
public interface IImageService
{
    Task<ImageSource?> LoadAsync(string url, int decodePixelWidth, TimeSpan? maxAge = null);
    Task PrefetchAsync(IEnumerable<string> urls, CancellationToken ct = default);
}

/// <summary>Системные диалоги выбора файлов.</summary>
public interface IDialogService
{
    /// <summary>Путь к выбранной картинке или null, если диалог закрыли.</summary>
    string? PickImageFile();

    /// <summary>Путь к выбранному файлу сборки Modrinth (.mrpack) или null, если диалог закрыли.</summary>
    string? PickModpackFile();

    /// <summary>Путь к выбранной папке или null, если диалог закрыли.</summary>
    string? PickFolder(string title, string? initialDirectory = null);
}

/// <summary>Открытие ссылок во внешнем браузере. Разрешены только http/https.</summary>
public interface IBrowserService
{
    bool TryOpen(string? url);
}

/// <summary>Закрытие лаунчера из ViewModel (например, перед заменой файлов при обновлении). Сами ViewModel-и от WPF не зависят.</summary>
public interface IAppShutdown
{
    void Shutdown();
}

/// <summary>Буфер обмена (например, код входа Microsoft).</summary>
public interface IClipboardService
{
    /// <returns>false — буфер занят другим приложением, текст не скопирован.</returns>
    bool TrySetText(string text);
}

/// <summary>Короткие уведомления поверх окна.</summary>
public interface IToastService
{
    void Show(string text, int milliseconds = 2600);
}
