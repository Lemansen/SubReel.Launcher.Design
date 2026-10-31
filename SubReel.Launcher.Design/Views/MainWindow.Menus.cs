using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SubReel.Launcher.Design.Views;

// Меню профиля и выпадающий список версий: открытие/закрытие с анимацией и поворот стрелок.
public partial class MainWindow
{
    private int _profileMenuToken;
    private int _vdropToken;

    // ───────────────────────── Меню профиля ─────────────────────────

    private void OpenProfileMenuAnimated()
    {
        _profileMenuToken++; // отменяет отложенное скрытие
        ProfileMenuHost.Visibility = Visibility.Visible;
        ProfileMenuCard.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        GrowCard(ProfileMenuCard);
        RotateChevron(ProfileChevron, 180);
    }

    private void CloseProfileMenuAnimated()
    {
        var token = ++_profileMenuToken;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            if (token == _profileMenuToken) ProfileMenuHost.Visibility = Visibility.Collapsed;
        };
        ProfileMenuCard.BeginAnimation(UIElement.OpacityProperty, fade);
        RotateChevron(ProfileChevron, 0);
    }

    // Форма «Добавить аккаунт» раскрылась — ставим в неё курсор (после того, как привязка сделала панель видимой).
    private void FocusAddAccountBox() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => AddAccountBox.Focus()));

    // ───────────────────────── Список версий над кнопкой «Версия» ─────────────────────────

    private void OpenVersionDropdownAnimated()
    {
        _vdropToken++; // отменяет отложенное скрытие

        PositionVersionDropdown();
        VDropList.ApplyTemplate();
        (VDropList.Template.FindName("VDropScroll", VDropList) as ScrollViewer)?.ScrollToTop();

        VDropHost.Visibility = Visibility.Visible;
        VDropCard.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        GrowCard(VDropCard);
        RotateChevron(VersionChevron, 180);
    }

    private void CloseVersionDropdownAnimated()
    {
        var token = ++_vdropToken;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            if (token == _vdropToken) VDropHost.Visibility = Visibility.Collapsed;
        };
        VDropCard.BeginAnimation(UIElement.OpacityProperty, fade);
        RotateChevron(VersionChevron, 0);
    }

    /// <summary>Список «вырастает» из кнопки «Версия» вверх и не вылезает за окно (ширина и высота подстраиваются под размер окна).</summary>
    private void PositionVersionDropdown()
    {
        if (!VersionButton.IsLoaded || RootGrid.ActualWidth <= 0) return;
        var origin = VersionButton.TranslatePoint(new Point(0, 0), RootGrid);
        var left = Math.Max(8, origin.X);
        VDropCard.Margin = new Thickness(left, 0, 0, Math.Max(8, RootGrid.ActualHeight - origin.Y + 10));
        VDropCard.Width = Math.Min(380, Math.Max(280, RootGrid.ActualWidth - left - 8));
        VDropCard.MaxHeight = Math.Max(200, origin.Y - 24);
    }

    // ───────────────────────── Общее ─────────────────────────

    private static void GrowCard(Border card)
    {
        if (card.RenderTransform is not ScaleTransform scale) return;
        var grow = new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = Anim.OutQuart };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private static void RotateChevron(FrameworkElement chevron, double angle)
    {
        if (chevron.RenderTransform is RotateTransform rotate)
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, TimeSpan.FromMilliseconds(260)) { EasingFunction = Anim.OutQuart });
    }
}
