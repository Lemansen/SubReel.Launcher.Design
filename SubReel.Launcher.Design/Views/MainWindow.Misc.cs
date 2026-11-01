using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SubReel.Launcher.Design.Views;

// Уведомление, индикатор подготовки игры и экран новостей: только анимации — данные и логика в ViewModel-ях.
public partial class MainWindow
{
    private int _toastToken;
    private int _progressAnimationToken;
    private int _newsPanelToken;
    private int _buildsPanelToken;
    private int _newsDetailToken;

    // ───────────────────────── Уведомление ─────────────────────────

    private void ApplyToast(bool visible)
    {
        var token = ++_toastToken;
        if (visible)
        {
            ToastHost.Visibility = Visibility.Visible;
            ToastHost.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) =>
        {
            if (token == _toastToken) ToastHost.Visibility = Visibility.Collapsed;
        };
        ToastHost.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ───────────────────────── Индикатор подготовки игры ─────────────────────────

    private void ApplyProgressPanel(bool expanded)
    {
        // .bottom-right.active: индикатор растягивается на всё свободное место между левыми кнопками и «Играть» за 0.6s
        var token = ++_progressAnimationToken;
        var startWidth = ProgressPanel.ActualWidth;
        var target = expanded ? Math.Max(0, ProgressColumn.ActualWidth - (ProgressPanel.Margin.Left + ProgressPanel.Margin.Right)) : 0;
        ProgressPanel.BeginAnimation(FrameworkElement.MaxWidthProperty, null);
        ProgressPanel.MaxWidth = startWidth; // фиксируем текущее значение, если анимация была прервана

        var width = new DoubleAnimation(startWidth, target, TimeSpan.FromMilliseconds(600)) { EasingFunction = Anim.OutQuart };
        width.Completed += (_, _) =>
        {
            if (token != _progressAnimationToken) return;
            // После раскрытия снимаем анимацию, чтобы панель тянулась при изменении размера окна.
            ProgressPanel.BeginAnimation(FrameworkElement.MaxWidthProperty, null);
            ProgressPanel.MaxWidth = expanded ? double.PositiveInfinity : 0;
        };
        ProgressPanel.BeginAnimation(FrameworkElement.MaxWidthProperty, width);
        ProgressPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(expanded ? 1 : 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = Anim.OutQuart });
    }

    // ───────────────────────── Новости ─────────────────────────

    private void OpenNewsAnimated()
    {
        _newsPanelToken++; // отменяет отложенное скрытие
        UpdateNewsLoadingBar();
        NewsPanel.Visibility = Visibility.Visible;
        NewsPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
        if (NewsPanel.RenderTransform is TranslateTransform shift)
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(500)) { EasingFunction = Anim.OutQuart });
    }

    private void CloseNewsAnimated()
    {
        var token = ++_newsPanelToken;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) =>
        {
            if (token == _newsPanelToken) NewsPanel.Visibility = Visibility.Collapsed;
        };
        NewsPanel.BeginAnimation(UIElement.OpacityProperty, fade);
        if (NewsPanel.RenderTransform is TranslateTransform shift)
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(40, TimeSpan.FromMilliseconds(400)) { EasingFunction = Anim.OutQuart });
    }

    // ───────────────────────── Создание сборки ─────────────────────────

    private void OpenBuildsAnimated()
    {
        _buildsPanelToken++; // отменяет отложенное скрытие
        BuildsPanel.Visibility = Visibility.Visible;
        BuildsPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
        if (BuildsPanel.RenderTransform is TranslateTransform shift)
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(500)) { EasingFunction = Anim.OutQuart });
    }

    private void CloseBuildsAnimated()
    {
        var token = ++_buildsPanelToken;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) =>
        {
            if (token == _buildsPanelToken) BuildsPanel.Visibility = Visibility.Collapsed;
        };
        BuildsPanel.BeginAnimation(UIElement.OpacityProperty, fade);
        if (BuildsPanel.RenderTransform is TranslateTransform shift)
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(40, TimeSpan.FromMilliseconds(400)) { EasingFunction = Anim.OutQuart });
    }

    private void OnNewsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NewsViewModel.IsBusy): SpinRefreshIcon(_vm.News.IsBusy); break;
            case nameof(NewsViewModel.Pane): UpdateNewsLoadingBar(); break;
            case nameof(NewsViewModel.IsDetailOpen): ApplyNewsDetail(_vm.News.IsDetailOpen); break;
        }
    }

    private void SpinRefreshIcon(bool busy)
    {
        if (NewsRefreshIcon.RenderTransform is not RotateTransform rotate) return;
        if (busy)
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
        else
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    // Полоска «Загружаем новости…» бегает, только пока видна.
    private void UpdateNewsLoadingBar()
    {
        if (NewsLoadingBar.RenderTransform is not TranslateTransform shift) return;
        if (_vm.News.Pane == NewsPane.Loading)
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 140, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        else
            shift.BeginAnimation(TranslateTransform.XProperty, null);
    }

    // Полная новость: список и вкладки скрываются привязкой, сама новость плавно появляется / гаснет.
    private void ApplyNewsDetail(bool open)
    {
        var token = ++_newsDetailToken;
        if (open)
        {
            NewsDetail.Visibility = Visibility.Visible;
            NewsDetail.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)));
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            if (token == _newsDetailToken) NewsDetail.Visibility = Visibility.Collapsed;
        };
        NewsDetail.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
