using Microsoft.Extensions.Logging;

namespace SubReel.Launcher.Design.Rendering;

/// <summary>
/// Сервисы для статических построителей оформления (<see cref="CosmeticsRenderer"/> вызывается из шаблонов и конвертеров XAML,
/// куда контейнер зависимости передать не может). Заполняется один раз в корне композиции; это единственное осознанное
/// исключение из «никаких глобальных сервисов».
/// </summary>
public static class RenderingServices
{
    public static IImageService? Images { get; private set; }
    public static ILogger? Logger { get; private set; }

    public static void Initialize(IImageService images, ILogger logger)
    {
        Images = images;
        Logger = logger;
    }
}
