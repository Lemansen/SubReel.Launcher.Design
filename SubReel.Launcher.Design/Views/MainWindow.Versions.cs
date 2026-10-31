using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SubReel.Launcher.Design.Views;

// Анимации экрана «Все версии» и страницы версии. Данные (карточки, поиск, страница) — в VersionsViewModel / VersionPageViewModel.
public partial class MainWindow
{
    private int _versionsToken;
    private int _pageToken;

    private IEnumerable<Button> VersionCards() =>
        VisualTree.Descendants<Button>(VersionsWrap).Where(b => b.DataContext is VersionCardViewModel);

    private void EnterVersionsAnimated()
    {
        _versionsToken++;

        VersionsScroll.ScrollToTop();
        VersionsPanel.Visibility = Visibility.Visible;
        VersionsPanel.UpdateLayout(); // карточки создаются шаблонами при первом показе — они нужны для анимации ниже

        VersionsPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
        if (VersionsPanel.RenderTransform is TranslateTransform panelShift)
            panelShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(140, 0, TimeSpan.FromMilliseconds(500)) { EasingFunction = Anim.OutQuart });

        // Поиск съезжает сверху, пока снизу выезжают карточки.
        var barDelay = TimeSpan.FromMilliseconds(180);
        VersionsSearchBar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)) { BeginTime = barDelay });
        if (VersionsSearchBar.RenderTransform is TranslateTransform barShift)
            barShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-34, 0, TimeSpan.FromMilliseconds(520)) { BeginTime = barDelay, EasingFunction = Anim.OutQuart });

        // Карточки выезжают снизу по очереди.
        var i = 0;
        foreach (var card in VersionCards())
        {
            var delay = TimeSpan.FromMilliseconds(120 + Math.Min(i++, 12) * 35);
            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = delay });
            // Transform, объявленный в DataTemplate, WPF замораживает — анимировать его нельзя; ставим свой.
            var shift = new TranslateTransform(0, 40);
            card.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(480)) { BeginTime = delay, EasingFunction = Anim.OutQuart });
        }
    }

    private void ExitVersionsAnimated()
    {
        var token = ++_versionsToken;

        Keyboard.ClearFocus();
        VersionsSearchBar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
        if (VersionsSearchBar.RenderTransform is TranslateTransform barHide)
            barHide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-34, TimeSpan.FromMilliseconds(300)) { EasingFunction = Anim.OutQuart });

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) =>
        {
            if (token == _versionsToken) VersionsPanel.Visibility = Visibility.Collapsed;
        };
        VersionsPanel.BeginAnimation(UIElement.OpacityProperty, fade);
        if (VersionsPanel.RenderTransform is TranslateTransform panelShift)
            panelShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(140, TimeSpan.FromMilliseconds(400)) { EasingFunction = Anim.OutQuart });
    }

    // ───────────────────────── Страница версии ─────────────────────────

    private void OpenPageAnimated()
    {
        _pageToken++; // отменяет отложенное скрытие страницы

        // Список гаснет и чуть уезжает вверх; поиск в нём не должен ловить ввод.
        Keyboard.ClearFocus();
        VersionsPanel.IsHitTestVisible = false;
        VersionsPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
        if (VersionsPanel.RenderTransform is TranslateTransform listShift)
            listShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-30, TimeSpan.FromMilliseconds(380)) { EasingFunction = Anim.OutQuart });

        // Страница выезжает снизу, её элементы появляются по очереди.
        var delay = TimeSpan.FromMilliseconds(150);
        VersionPage.Visibility = Visibility.Visible;
        VersionPage.IsHitTestVisible = true;
        VersionPage.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { BeginTime = delay });
        if (VersionPage.RenderTransform is TranslateTransform pageShift)
            pageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(550)) { BeginTime = delay, EasingFunction = Anim.OutQuart });
        PlayPageIntro();
    }

    /// <summary>backToList: true — вернуться к карточкам; false — страница просто гаснет (список закрывается сам).</summary>
    private void ClosePageAnimated(bool backToList)
    {
        var token = ++_pageToken;

        VersionPage.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) =>
        {
            if (token == _pageToken) VersionPage.Visibility = Visibility.Collapsed;
        };
        VersionPage.BeginAnimation(UIElement.OpacityProperty, fade);
        if (VersionPage.RenderTransform is TranslateTransform pageShift)
            pageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(40, TimeSpan.FromMilliseconds(350)) { EasingFunction = Anim.OutQuart });

        VersionsPanel.IsHitTestVisible = true;
        if (!backToList) return;

        var delay = TimeSpan.FromMilliseconds(120);
        VersionsPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { BeginTime = delay });
        if (VersionsPanel.RenderTransform is TranslateTransform listShift)
            listShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(450)) { BeginTime = delay, EasingFunction = Anim.OutQuart });
    }

    private void PlayPageIntro()
    {
        var items = new FrameworkElement[] { VPLabel, VPTitle, VPMeta, VPSummary, VPVersionsBox, VPSelectButton };
        for (var i = 0; i < items.Length; i++)
        {
            var element = items[i];
            var delay = TimeSpan.FromMilliseconds(260 + i * 70);
            // Сначала снимаем прошлые анимации и прячем элемент: иначе до своей очереди он будет виден.
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 0;
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { BeginTime = delay });
            if (element.RenderTransform is TranslateTransform shift)
            {
                shift.BeginAnimation(TranslateTransform.YProperty, null);
                shift.Y = 24;
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(700)) { BeginTime = delay, EasingFunction = Anim.OutQuart });
            }
        }
    }

    /// <summary>Выбрали другую версию на странице: дата и описание мягко «мигают».</summary>
    private void BlinkPageTexts()
    {
        var blink = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320));
        VPMeta.BeginAnimation(UIElement.OpacityProperty, blink);
        VPSummary.BeginAnimation(UIElement.OpacityProperty, blink);
    }
}
