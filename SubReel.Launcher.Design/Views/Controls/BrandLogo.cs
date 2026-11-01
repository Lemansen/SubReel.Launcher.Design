using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace SubReel.Launcher.Design.Views.Controls;

/// <summary>
/// Логотип источника на карточке: картинка <c>Assets/Logos/&lt;Logo&gt;.png</c>, а если её нет — запасная иконка на цветной плашке.
/// </summary>
public sealed class BrandLogo : Border
{
    public static readonly DependencyProperty LogoProperty = DependencyProperty.Register(
        nameof(Logo), typeof(string), typeof(BrandLogo), new PropertyMetadata(null, OnChanged));
    public static readonly DependencyProperty FallbackProperty = DependencyProperty.Register(
        nameof(Fallback), typeof(Geometry), typeof(BrandLogo), new PropertyMetadata(null, OnChanged));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(BrandLogo), new PropertyMetadata(Color.FromRgb(0x4C, 0x85, 0xFF), OnChanged));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(BrandLogo), new PropertyMetadata(64.0, OnChanged));

    public string? Logo { get => (string?)GetValue(LogoProperty); set => SetValue(LogoProperty, value); }
    public Geometry? Fallback { get => (Geometry?)GetValue(FallbackProperty); set => SetValue(FallbackProperty, value); }
    public Color Accent { get => (Color)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((BrandLogo)d).Rebuild();

    private void Rebuild()
    {
        Width = Size;
        Height = Size;
        var image = LogoCatalog.Find(Logo);
        if (image is not null)
        {
            Background = null;
            BorderThickness = new Thickness(0);
            CornerRadius = new CornerRadius(0);
            Child = new Image { Source = image, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            return;
        }

        var brush = new SolidColorBrush(Accent);
        CornerRadius = new CornerRadius(Size * 0.28);
        BorderThickness = new Thickness(1);
        Background = new SolidColorBrush(Color.FromArgb(0x24, Accent.R, Accent.G, Accent.B));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, Accent.R, Accent.G, Accent.B));
        Child = new Viewbox
        {
            Width = Size * 0.5,
            Height = Size * 0.5,
            Child = new Canvas
            {
                Width = 24,
                Height = 24,
                Children =
                {
                    new ShapePath
                    {
                        Data = Fallback,
                        Stroke = brush,
                        StrokeThickness = 2,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                    },
                },
            },
        };
    }
}
