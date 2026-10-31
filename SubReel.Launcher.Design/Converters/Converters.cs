using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SubReel.Launcher.Design.Converters;

/// <summary>bool → Visible / Collapsed (Invert — наоборот).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        return flag != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>null или пустая строка → Collapsed, всё остальное → Visible (Invert — наоборот).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value is not null && value is not "";
        return has != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Значение перечисления совпало с ConverterParameter (по имени) → Visible, иначе Collapsed.</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>bool → одно из двух значений (например, Tag = «active» / null для подсветки кнопки стилем).</summary>
public sealed class BoolToStringConverter : IValueConverter
{
    public object? TrueValue { get; set; }
    public object? FalseValue { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? TrueValue : FalseValue;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Значение перечисления → его имя строчными буквами (для триггеров стиля по Tag: «loading», «ready»).</summary>
public sealed class EnumToTagConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value?.ToString()?.ToLowerInvariant();
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Картинка → кисть, растянутая по размеру элемента (как фон-заглушка).</summary>
public sealed class ImageToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ImageSource image ? new ImageBrush(image) { Stretch = Stretch.UniformToFill } : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>bool → !bool (например, кнопка «Обновить» недоступна, пока идёт загрузка).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
