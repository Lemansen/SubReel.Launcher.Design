using SubReel.Launcher.Domain.Settings;

namespace SubReel.Launcher.Application.Abstractions;

/// <summary>Настройки лаунчера (аккаунты, выбранная версия, недавние версии).</summary>
public interface ISettingsService
{
    LauncherSettings Current { get; }

    /// <summary>Сохраняет настройки атомарно. Ошибка записи логируется и не роняет лаунчер; прежний файл остаётся целым.</summary>
    void Save();
}
