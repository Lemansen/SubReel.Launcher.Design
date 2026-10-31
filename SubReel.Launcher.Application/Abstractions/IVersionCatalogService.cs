using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Application.Abstractions;

/// <summary>Каталог релизов Minecraft и картинок обновлений.</summary>
public interface IVersionCatalogService
{
    /// <summary>Загружает каталог. Если источник недоступен, возвращает встроенный (<see cref="VersionCatalog.IsOffline"/>). Отмена пробрасывается.</summary>
    Task<VersionCatalog> LoadAsync(CancellationToken ct = default);

    VersionCatalog CreateFallback();
}
