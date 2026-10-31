using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Domain.Settings;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Infrastructure.Settings;

/// <summary>
/// Настройки лаунчера в <c>%AppData%\SubReel\design-settings.json</c> (формат прежний; добавлено только поле SchemaVersion).
/// Данные пользователя не теряются: повреждённый файл откладывается, а не затирается; настройки из более новой версии
/// лаунчера и недоступный для чтения файл не перезаписываются.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly AppPaths _paths;
    private readonly JsonFileStore _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly bool _saveEnabled = true;

    public SettingsService(AppPaths paths, JsonFileStore store, ILogger<SettingsService> logger)
    {
        _paths = paths;
        _store = store;
        _logger = logger;

        var result = store.Read<LauncherSettings>(paths.SettingsFile, Options);
        var needsSave = false;

        switch (result.Status)
        {
            case StoreReadStatus.Loaded:
            case StoreReadStatus.RecoveredFromBackup:
                Current = result.Value!;
                if (Current.SchemaVersion > LauncherSettings.CurrentSchemaVersion)
                {
                    _saveEnabled = false;
                    _logger.LogWarning(
                        "Настройки записаны более новой версией лаунчера (формат {Found}, поддерживается {Supported}). Они не будут перезаписаны.",
                        Current.SchemaVersion, LauncherSettings.CurrentSchemaVersion);
                }
                else
                {
                    needsSave = LauncherSettingsMigrator.Migrate(Current) || result.Status == StoreReadStatus.RecoveredFromBackup;
                }
                break;

            case StoreReadStatus.Unavailable:
                Current = NewDefaults();
                _saveEnabled = false;
                break;

            default: // Missing, Corrupt
                Current = NewDefaults();
                if (result.Status == StoreReadStatus.Corrupt)
                    _logger.LogWarning("Файл настроек был повреждён, запущен с настройками по умолчанию. Исходный файл сохранён рядом.");
                break;
        }

        if (needsSave) Save();
    }

    public LauncherSettings Current { get; }

    public void Save()
    {
        if (!_saveEnabled)
        {
            _logger.LogWarning("Сохранение настроек отключено, чтобы не затереть существующий файл.");
            return;
        }

        Current.SchemaVersion = LauncherSettings.CurrentSchemaVersion;
        _store.Write(_paths.SettingsFile, Current, Options);
    }

    private static LauncherSettings NewDefaults() => new() { SchemaVersion = LauncherSettings.CurrentSchemaVersion };
}
