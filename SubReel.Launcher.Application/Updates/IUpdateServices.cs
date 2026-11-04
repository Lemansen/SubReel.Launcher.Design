namespace SubReel.Launcher.Application.Updates;

/// <summary>Версия запущенного лаунчера (из сборки). Отдельный контракт, чтобы логику обновлений можно было проверять тестами.</summary>
public interface IAppVersion
{
    Version Current { get; }
}

/// <summary>
/// Автообновление лаунчера: проверка манифеста, «Позже», скачивание и запуск программы замены файлов.
/// Сбои сети не бросают исключений: причина в <see cref="LastError"/>, лаунчер продолжает работать.
/// </summary>
public interface ILauncherUpdateService
{
    Version CurrentVersion { get; }

    /// <summary>Адрес манифеста задан в конфигурации.</summary>
    bool IsConfigured { get; }

    /// <summary>Последняя найденная версия новее текущей; null — обновлений нет или проверка не удалась.</summary>
    LauncherUpdateInfo? Available { get; }

    /// <summary>Показывать ли баннер: версия есть и не отложена (обязательные откладывать нельзя).</summary>
    bool IsBannerVisible { get; }

    bool IsChecking { get; }
    bool IsApplying { get; }

    /// <summary>Текст последней неудачной проверки; null — последняя проверка прошла (или её ещё не было).</summary>
    string? LastError { get; }

    DateTime? LastCheckedUtc { get; }

    /// <summary>Изменилось любое из свойств выше. Может приходить из фонового потока.</summary>
    event EventHandler? StateChanged;

    Task CheckAsync(CancellationToken ct = default);

    /// <summary>«Позже»: прячет баннер этой версии на несколько дней. Более новая версия вернёт его раньше срока.</summary>
    void Postpone();

    /// <summary>Скачивает, проверяет и распаковывает обновление, запускает программу замены. При успехе лаунчер нужно закрыть.</summary>
    Task<UpdateApplyResult> ApplyAsync(IProgress<UpdateProgress>? progress = null, CancellationToken ct = default);
}
