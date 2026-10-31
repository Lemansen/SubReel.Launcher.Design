namespace SubReel.Launcher.Design.ViewModels;

/// <summary>Короткое уведомление поверх окна. Само гаснет; новое уведомление заменяет старое.</summary>
public sealed class ToastViewModel : ObservableObject, IToastService
{
    private int _token;
    private string _text = "";
    private bool _isVisible;

    public string Text { get => _text; private set => SetProperty(ref _text, value); }
    public bool IsVisible { get => _isVisible; private set => SetProperty(ref _isVisible, value); }

    public void Show(string text, int milliseconds = 2600)
    {
        var token = ++_token;
        Text = text;
        IsVisible = true;
        _ = HideAfterAsync(token, milliseconds);
    }

    // Возвращает Task вместо async void: сбой не роняет процесс, а попадает в обработчик необработанных задач (журнал).
    private async Task HideAfterAsync(int token, int milliseconds)
    {
        await Task.Delay(Math.Max(0, milliseconds));
        if (token != _token) return; // показали новое уведомление — старое не трогаем
        IsVisible = false;
    }
}
