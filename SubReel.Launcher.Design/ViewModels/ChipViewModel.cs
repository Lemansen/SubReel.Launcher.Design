using System.Windows.Input;
using System.Windows.Media;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>Кнопка-вариант (недавняя версия, вкладка новостей, стиль оформления). Неизменяема: список вариантов пересоздаётся целиком.</summary>
public sealed class ChipViewModel
{
    public ChipViewModel(string title, bool isSelected, ICommand command, Brush? labelBrush = null)
    {
        Title = title;
        IsSelected = isSelected;
        Command = command;
        LabelBrush = labelBrush ?? ThemeBrushes.TextMain;
    }

    public string Title { get; }
    public bool IsSelected { get; }
    public ICommand Command { get; }

    /// <summary>Кисть подписи: у вариантов ника показывает, как будет выглядеть ник.</summary>
    public Brush LabelBrush { get; }
}
