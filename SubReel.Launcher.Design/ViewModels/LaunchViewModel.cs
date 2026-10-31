using System.Windows.Input;
using Microsoft.Extensions.Logging;

namespace SubReel.Launcher.Design.ViewModels;

public enum LaunchState { Idle, Loading, Ready }

/// <summary>Кнопка «Играть» и индикатор подготовки рядом с ней (пока демо: настоящий запуск игры не подключён).</summary>
public sealed class LaunchViewModel : ObservableObject
{
    private const string IdleStatus = "Проверка файлов...";

    private readonly VersionStore _store;
    private readonly NavigationService _nav;
    private readonly IToastService _toast;
    private readonly ILogger<LaunchViewModel> _logger;

    private LaunchState _state;
    private string _playText = "ИГРАТЬ";
    private double _progress;
    private string _progressText = "0%";
    private string _statusText = IdleStatus;
    private bool _isProgressExpanded;

    public LaunchViewModel(VersionStore store, NavigationService nav, IToastService toast, ILogger<LaunchViewModel> logger)
    {
        _store = store;
        _nav = nav;
        _toast = toast;
        _logger = logger;
        PlayCommand = new AsyncRelayCommand(PlayAsync, onError: OnPlayFailed);

        // Выбрали другую версию — «готово» больше не верно (если подготовка не идёт прямо сейчас).
        store.VersionSelected += (_, _) => Reset();
    }

    public LaunchState State { get => _state; private set => SetProperty(ref _state, value); }
    public string PlayText { get => _playText; private set => SetProperty(ref _playText, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>Панель прогресса раскрыта между левыми кнопками и «Играть» (окно анимирует раскрытие).</summary>
    public bool IsProgressExpanded { get => _isProgressExpanded; private set => SetProperty(ref _isProgressExpanded, value); }

    public ICommand PlayCommand { get; }

    private void OnPlayFailed(Exception ex)
    {
        _logger.LogError(ex, "Сбой при подготовке запуска версии {Version}.", _store.SelectedVersion);
        Reset();
        _toast.Show("Не удалось подготовить запуск. Подробности в журнале.");
    }

    private void Reset()
    {
        if (State == LaunchState.Loading) return;
        State = LaunchState.Idle;
        PlayText = "ИГРАТЬ";
        IsProgressExpanded = false;
        Progress = 0;
        ProgressText = "0%";
        StatusText = IdleStatus;
    }

    private async Task PlayAsync()
    {
        if (State == LaunchState.Loading) return;

        if (State == LaunchState.Ready)
        {
            _nav.ShowOverlay(new MessageOverlayViewModel("Запуск Minecraft", new[]
            {
                new InfoBlockItem("Демо-режим", $"Интерфейс готов к запуску Minecraft {_store.VersionLabel}. Подключение игрового ядра будет следующим этапом."),
            }));
            return;
        }

        // Неустановленную версию не «запускаем»: ждём результат поиска установленных версий и честно говорим, что её нет.
        if (string.IsNullOrEmpty(_store.SelectedVersion)) return; // список версий ещё не загружен
        if (!_store.InstalledScanned) await _store.ScanTask;
        if (_store.SelectedInstall is null)
        {
            _nav.ShowOverlay(new MessageOverlayViewModel("Версия не установлена", new[]
            {
                new InfoBlockItem($"Minecraft {_store.SelectedVersion}",
                    $"Этой версии нет в папке игры ({_store.GameDirectory}\\versions), поэтому запустить её нельзя. Установка версий и загрузчиков появится вместе с игровым ядром."),
                new InfoBlockItem("Что сделать сейчас", "Выбери установленную версию в списке «Версия» в нижней панели."),
            }, "Понятно", new RelayCommand(_nav.CloseOverlay)));
            return;
        }

        State = LaunchState.Loading;
        IsProgressExpanded = true;
        PlayText = "ЗАГРУЗКА...";
        Progress = 0;
        ProgressText = "0%";

        var finished = false;
        try
        {
            for (var progress = 0; progress <= 100; progress += 4)
            {
                Progress = progress;
                ProgressText = $"{progress}%";
                StatusText = progress < 28 ? "Проверка файлов..."
                    : progress < 58 ? "Проверка обновлений..."
                    : progress < 82 ? "Подготовка игрового окружения..."
                    : "Завершение подготовки...";
                await Task.Delay(65);
            }

            finished = true;
            State = LaunchState.Ready;
            PlayText = "ЗАПУСК";
            StatusText = $"Minecraft {_store.VersionLabel} · готово (демо)";
        }
        finally
        {
            if (!finished) State = LaunchState.Idle;
        }
    }
}
