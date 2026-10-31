using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SubReel.Launcher.Design.Infrastructure;

/// <summary>Базовый класс для всего, что показывается в интерфейсе через привязки (INotifyPropertyChanged).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Записывает значение и сообщает об изменении, только если оно действительно поменялось.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
