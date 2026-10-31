using System.Windows.Input;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>Содержимое модального окна. Какой экран нарисовать, решает View по типу (DataTemplate).</summary>
public abstract class OverlayViewModel : ObservableObject
{
    public abstract string Title { get; }
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
    private double _ramGb = 4;
    private bool _autoUpdate = true;
    private bool _closeAfterLaunch = true;

    public override string Title => "Настройки";

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

/// <summary>Создание сборки (демо: файлы не загружаются).</summary>
public sealed class ModpackOverlayViewModel : OverlayViewModel
{
    private readonly NavigationService _nav;
    private string _name = "Моя сборка";
    private string _selectedLoader = "Fabric";

    public ModpackOverlayViewModel(NavigationService nav)
    {
        _nav = nav;
        CreateCommand = new RelayCommand(Create);
    }

    public override string Title => "Создать сборку";

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public IReadOnlyList<string> Loaders { get; } = new[] { "Vanilla", "Fabric", "Forge", "NeoForge", "Quilt" };
    public string SelectedLoader { get => _selectedLoader; set => SetProperty(ref _selectedLoader, value); }

    public ICommand CreateCommand { get; }

    private void Create() =>
        _nav.ShowOverlay(new MessageOverlayViewModel("Сборка подготовлена", new[]
        {
            new InfoBlockItem(Name, $"Профиль с загрузчиком {SelectedLoader} показан в демо-режиме. Файлы не загружаются."),
        }));
}
