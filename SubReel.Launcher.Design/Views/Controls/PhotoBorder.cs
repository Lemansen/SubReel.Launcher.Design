using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SubReel.Launcher.Design.Views.Controls;

/// <summary>
/// Слой с фотографией: пока <see cref="Source"/> пуст, слой прозрачен (виден фон под ним — например, пиксельная заглушка);
/// когда картинка появляется, она плавно проявляется. Умеет «встать на место» из приближения (SettleFrom) и приближаться при наведении (IsZoomed).
/// Приближается сама картинка, а не элемент, поэтому скруглённые углы остаются ровными.
/// </summary>
public class PhotoBorder : Border
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(PhotoBorder), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty FadeInMillisecondsProperty = DependencyProperty.Register(
        nameof(FadeInMilliseconds), typeof(int), typeof(PhotoBorder), new PropertyMetadata(380));

    /// <summary>Начальное приближение (1 — без «наезда»). За SettleMilliseconds картинка встаёт на место.</summary>
    public static readonly DependencyProperty SettleFromProperty = DependencyProperty.Register(
        nameof(SettleFrom), typeof(double), typeof(PhotoBorder), new PropertyMetadata(1.0));

    public static readonly DependencyProperty SettleMillisecondsProperty = DependencyProperty.Register(
        nameof(SettleMilliseconds), typeof(int), typeof(PhotoBorder), new PropertyMetadata(1200));

    /// <summary>При смене одной картинки на другую старая сначала гаснет.</summary>
    public static readonly DependencyProperty CrossFadeProperty = DependencyProperty.Register(
        nameof(CrossFade), typeof(bool), typeof(PhotoBorder), new PropertyMetadata(false));

    public static readonly DependencyProperty IsZoomedProperty = DependencyProperty.Register(
        nameof(IsZoomed), typeof(bool), typeof(PhotoBorder), new PropertyMetadata(false, OnZoomChanged));

    public static readonly DependencyProperty HoverScaleProperty = DependencyProperty.Register(
        nameof(HoverScale), typeof(double), typeof(PhotoBorder), new PropertyMetadata(1.08));

    private int _token;

    public PhotoBorder()
    {
        Opacity = 0;
        IsHitTestVisible = false;
    }

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public int FadeInMilliseconds { get => (int)GetValue(FadeInMillisecondsProperty); set => SetValue(FadeInMillisecondsProperty, value); }
    public double SettleFrom { get => (double)GetValue(SettleFromProperty); set => SetValue(SettleFromProperty, value); }
    public int SettleMilliseconds { get => (int)GetValue(SettleMillisecondsProperty); set => SetValue(SettleMillisecondsProperty, value); }
    public bool CrossFade { get => (bool)GetValue(CrossFadeProperty); set => SetValue(CrossFadeProperty, value); }
    public bool IsZoomed { get => (bool)GetValue(IsZoomedProperty); set => SetValue(IsZoomedProperty, value); }
    public double HoverScale { get => (double)GetValue(HoverScaleProperty); set => SetValue(HoverScaleProperty, value); }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PhotoBorder)d).ApplySource(e.NewValue as ImageSource);

    private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PhotoBorder)d).Zoom((bool)e.NewValue);

    private void ApplySource(ImageSource? source)
    {
        var token = ++_token;
        if (source is null)
        {
            BeginAnimation(OpacityProperty, null);
            Background = null;
            Opacity = 0;
            return;
        }

        if (CrossFade && Background is not null && Opacity > 0)
        {
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += (_, _) =>
            {
                if (token == _token) Show(source);
            };
            BeginAnimation(OpacityProperty, fadeOut);
            return;
        }
        Show(source);
    }

    private void Show(ImageSource source)
    {
        var from = SettleFrom;
        var zoom = new ScaleTransform(from, from, 0.5, 0.5);
        Background = new ImageBrush(source) { Stretch = Stretch.UniformToFill, RelativeTransform = zoom };

        if (from != 1.0 && SettleMilliseconds > 0)
        {
            var settle = new DoubleAnimation(from, 1.0, TimeSpan.FromMilliseconds(SettleMilliseconds)) { EasingFunction = Anim.OutCubic };
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
        }

        if (FadeInMilliseconds > 0)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeInMilliseconds)));
        }
        else
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        }
    }

    private void Zoom(bool zoomed)
    {
        if (Background is not ImageBrush { RelativeTransform: ScaleTransform zoom }) return;
        var anim = new DoubleAnimation(zoomed ? HoverScale : 1.0, TimeSpan.FromMilliseconds(380)) { EasingFunction = Anim.OutQuart };
        zoom.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        zoom.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }
}
