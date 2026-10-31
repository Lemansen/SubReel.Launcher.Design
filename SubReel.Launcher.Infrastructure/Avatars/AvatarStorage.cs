using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Infrastructure.Avatars;

/// <summary>Копирует свои аватарки в %AppData%\SubReel\avatars (то же место, что и раньше).</summary>
public sealed class AvatarStorage : IAvatarStorage
{
    private readonly AppPaths _paths;
    private readonly ILogger<AvatarStorage> _logger;

    public AvatarStorage(AppPaths paths, ILogger<AvatarStorage> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public string? Save(string accountName, string sourcePath)
    {
        try
        {
            Directory.CreateDirectory(_paths.AvatarsDirectory);
            var target = Path.Combine(_paths.AvatarsDirectory, accountName + Path.GetExtension(sourcePath).ToLowerInvariant());
            File.Copy(sourcePath, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Не удалось сохранить аватарку аккаунта {Account} из {Source}.", accountName, sourcePath);
            return null;
        }
    }
}
