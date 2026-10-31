using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SubReel.Launcher.Design.Views;

// Модальное окно: появление и закрытие. Что внутри — решает Nav.Overlay и DataTemplate.
public partial class MainWindow
{
    private void ShowOverlay()
    {
        OverlayPanel.Opacity = 0;
        OverlayCard.Opacity = 0;
        OverlayCard.RenderTransform = new ScaleTransform(0.96, 0.96);
        OverlayPanel.Visibility = Visibility.Visible;
        OverlayPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        OverlayCard.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        if (OverlayCard.RenderTransform is ScaleTransform scale)
        {
            var grow = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = Anim.OutCubic };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    private void HideOverlay()
    {
        OverlayPanel.Visibility = Visibility.Collapsed;
        OverlayPanel.BeginAnimation(UIElement.OpacityProperty, null);
        OverlayCard.BeginAnimation(UIElement.OpacityProperty, null);
    }
}
