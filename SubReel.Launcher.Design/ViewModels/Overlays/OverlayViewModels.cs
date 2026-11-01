using System.Windows.Input;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>Содержимое модального окна. Какой экран нарисовать, решает View по типу (DataTemplate).</summary>
public abstract class OverlayViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Ширина и предельная высота карточки окна; большие экраны («Аккаунты») переопределяют.</summary>
    public virtual double CardWidth => 680;
    public virtual double CardMaxHeight => 520;
}

/// <summary>Блок «заголовок + пояснение».</summary>
public sealed record InfoBlockItem(string Title, string Detail);

/// <summary>Простое сообщение: несколько блоков текста и, при необходимости, одна кнопка.</summary>
public sealed class MessageOverlayViewModel : OverlayViewModel
{
    public MessageOverlayViewModel(string title, IReadOnlyList<InfoBlockItem> blocks, string? buttonText = null, ICommand? buttonCommand = null)
    {
        Title = title;
        Blocks = blocks;
        ButtonText = buttonText;
        ButtonCommand = buttonCommand;
    }

    public override string Title { get; }
    public IReadOnlyList<InfoBlockItem> Blocks { get; }
    public string? ButtonText { get; }
    public ICommand? ButtonCommand { get; }
    public bool HasButton => ButtonText is not null && ButtonCommand is not null;
}

/// <summary>Настройки (демо: значения пока нигде не сохраняются, как и в прежней версии).</summary>
public sealed class SettingsOverlayViewModel : OverlayViewModel
{
    private readonly IDataLocationService _location;
    private readonly IDialogService _dialogs;
    private readonly IToastService _toast;
    private double _ramGb = 4;
    private bool _autoUpdate = true;
    private bool _closeAfterLaunch = true;

    public SettingsOverlayViewModel(IDataLocationService location, IDialogService dialogs, IToastService toast)
    {
        _location = location;
        _dialogs = dialogs;
        _toast = toast;
        PickFolderCommand = new RelayCommand(PickFolder);
        ResetFolderCommand = new RelayCommand(ResetFolder, () => _location.IsCustom || _location.PendingRoot is not null);
    }

    public override string Title => "Настройки";

    /// <summary>Папка данных, с которой лаунчер работает сейчас.</summary>
    public string DataFolderPath => _location.CurrentRoot;

    /// <summary>Выбранная папка, которая применится после перезапуска; пусто — смены не ожидается.</summary>
    public string PendingText => _location.PendingRoot is { } pending
        ? $"После перезапуска лаунчера данные переедут в «{pending}». Перенос выполнится автоматически."
        : "";

    public bool HasPending => _location.PendingRoot is not null;

    public ICommand PickFolderCommand { get; }
    public ICommand ResetFolderCommand { get; }

    private void PickFolder()
    {
        var chosen = _dialogs.PickFolder("Выбери папку для данных SubReel", _location.CurrentRoot);
        if (chosen is null) return;
        Report(_location.Change(chosen));
    }

    private void ResetFolder() => Report(_location.ResetToDefault());

    private void Report(DataLocationChange change)
    {
        if (!change.Succeeded)
        {
            _toast.Show(change.Error switch
            {
                DataLocationError.NotAbsolute => "Нужен полный путь к папке, например D:\\SubReel.",
                DataLocationError.NestedInCurrent => "Эта папка лежит внутри текущей папки данных. Выбери другую.",
                DataLocationError.ContainsCurrent => "Текущая папка данных лежит внутри выбранной. Выбери другую.",
                _ => "В эту папку нельзя записать. Выбери другую.",
            }, 4000);
            return;
        }

        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(HasPending));
        if (ResetFolderCommand is RelayCommand reset) reset.NotifyCanExecuteChanged();
        if (!HasPending) _toast.Show("Смена папки данных отменена.");
    }

    public double RamGb
    {
        get => _ramGb;
        set
        {
            if (!SetProperty(ref _ramGb, value)) return;
            OnPropertyChanged(nameof(RamLabel));
        }
    }

    public string RamLabel => $"{RamGb:0} ГБ выделено для игры";

    public bool AutoUpdate { get => _autoUpdate; set => SetProperty(ref _autoUpdate, value); }
    public bool CloseAfterLaunch { get => _closeAfterLaunch; set => SetProperty(ref _closeAfterLaunch, value); }
}
