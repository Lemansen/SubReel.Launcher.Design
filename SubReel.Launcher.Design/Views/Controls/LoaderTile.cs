using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace SubReel.Launcher.Design.Views.Controls;

/// <summary>Иконка загрузчика: у ванили — кубик, у остальных — монограмма в цвете загрузчика. При желании заменяется на логотипы.</summary>
public sealed class LoaderTile : Border
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(LoaderKind), typeof(LoaderTile), new PropertyMetadata(LoaderKind.Vanilla, OnChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(LoaderTile), new PropertyMetadata(34.0, OnChanged));

    public LoaderTile()
    {
        VerticalAlignment = VerticalAlignment.Center;
        BorderThickness = new Thickness(1);
        Rebuild();
    }

    public LoaderKind Kind { get => (LoaderKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((LoaderTile)d).Rebuild();

    private void Rebuild()
    {
        var size = Size;
        var color = LoaderAccent.ColorFor(Kind);
        var brush = new SolidColorBrush(color);

        var logo = LogoCatalog.Find(LogoCatalog.NameFor(Kind));
        if (logo is not null)
        {
            // Есть логотип загрузчика (Assets/Logos): рисуем его без плашки.
            Width = size;
            Height = size;
            CornerRadius = new CornerRadius(0);
            Background = null;
            BorderBrush = null;
            Child = new Image { Source = logo, Stretch = Stretch.Uniform, Margin = new Thickness(size * 0.06), SnapsToDevicePixels = true };
            return;
        }

        UIElement glyph;
        if (Kind == LoaderKind.Vanilla)
        {
            glyph = new Viewbox
            {
                Width = size * 0.5,
                Height = size * 0.5,
                Child = new Canvas
                {
                    Width = 24,
                    Height = 24,
                    Children =
                    {
                        new ShapePath
                        {
                            Data = System.Windows.Application.Current?.TryFindResource("IconCube") as Geometry,
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
        else
        {
            glyph = new TextBlock
            {
                Text = LoaderInfo.Monogram(Kind),
                FontSize = size * 0.36,
                FontWeight = FontWeights.Black,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(size * 0.3);
        Background = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B));
        Child = glyph;
    }
}
