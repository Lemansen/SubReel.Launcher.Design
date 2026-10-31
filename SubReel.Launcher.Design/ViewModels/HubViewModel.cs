using System.Windows.Input;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>SubReel Studio Hub: пока раздел честно помечен «В разработке» — карточки ничего не открывают, а показывают уведомление.</summary>
public sealed class HubViewModel
{
    private readonly IToastService _toast;

    public HubViewModel(IToastService toast)
    {
        _toast = toast;
        OpenToolCommand = new RelayCommand<string>(OpenTool);
    }

    /// <summary>CommandParameter — название инструмента.</summary>
    public ICommand OpenToolCommand { get; }

    private void OpenTool(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        _toast.Show(name == "Другие инструменты" ? "Новые инструменты пока в разработке" : $"{name} пока в разработке");
    }
}
