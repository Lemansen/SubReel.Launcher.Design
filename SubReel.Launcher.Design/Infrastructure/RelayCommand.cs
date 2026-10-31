using System.Windows.Input;

namespace SubReel.Launcher.Design.Infrastructure;

/// <summary>Команда без параметра: кнопка в XAML привязывается к ней через Command="{Binding ...}".</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public void NotifyCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}

/// <summary>Команда с параметром (CommandParameter).</summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Predicate<T?>? _canExecute;

    public RelayCommand(Action<T?> execute, Predicate<T?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(Convert(parameter)) ?? true;
    public void Execute(object? parameter) => _execute(Convert(parameter));

    private static T? Convert(object? parameter) => parameter is T value ? value : default;
}

/// <summary>
/// Асинхронная команда. Если задан <c>onError</c>, исключение команды передаётся ему и процесс продолжает работу;
/// без обработчика исключение, как и раньше, всплывает в UI-поток и попадает в журнал необработанных исключений.
/// Отмена (<see cref="OperationCanceledException"/>) — штатное завершение и ошибкой не считается.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public async void Execute(object? parameter)
    {
        try
        {
            await _execute();
        }
        catch (OperationCanceledException)
        {
            // отмена — штатный сценарий
        }
        catch (Exception ex) when (_onError is not null)
        {
            _onError(ex);
        }
    }
}
