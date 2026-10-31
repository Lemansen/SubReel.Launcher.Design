using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SubReel.Launcher.Design.Views;

// Главная: вступительные анимации, карточка об обновлении, фон-картинка, подсказка прокрутки, индикатор страниц и Hub.
public partial class MainWindow
{
    private static readonly Color DotActive = Color.FromRgb(0x78, 0xC8, 0xFF);
    private static readonly Color DotIdle = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);

    private bool _infoShown;
    private int _heroSwapToken;

    // ───────────────────────── Появление элементов ─────────────────────────

    /// <summary>Чип, заголовок, описание и новости по очереди всплывают снизу.</summary>
    private void PlayHeroIntro()
    {
        var items = new FrameworkElement[] { HeroChip, HeroTitle, HeroSub, NewsChipBtn };
        for (var i = 0; i < items.Length; i++)
        {
            var element = items[i];
            var delay = TimeSpan.FromMilliseconds(150 + i * 110);
            element.Opacity = 0;
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)) { BeginTime = delay });
            if (element.RenderTransform is TranslateTransform shift)
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(850)) { BeginTime = delay, EasingFunction = Anim.OutQuart });
        }

        ScrollHint.Opacity = 0;
        ScrollHint.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)) { BeginTime = TimeSpan.FromMilliseconds(900) });
        StartScrollHint();
    }

    /// <summary>Баннер и карточки инструментов по очереди всплывают снизу — как элементы главной.</summary>
    private void PlayHubIntro()
    {
        var items = new List<FrameworkElement> { HubBanner };
        items.AddRange(HubCards.Children.OfType<FrameworkElement>());

        for (var i = 0; i < items.Count; i++)
        {
            var element = items[i];
            if (element.RenderTransform is not TranslateTransform shift)
            {
                shift = new TranslateTransform();
                element.RenderTransform = shift;
            }

            var delay = TimeSpan.FromMilliseconds(40 + i * 80);
            element.Opacity = 0;
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { BeginTime = delay });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(700)) { BeginTime = delay, EasingFunction = Anim.OutQuart });
        }
    }

    // ───────────────────────── Карточка об обновлении ─────────────────────────

    // Тексты в карточке уже обновлены привязками; здесь только анимация появления.
    private void ShowInfoCard()
    {
        if (!_infoShown)
        {
            _infoShown = true;
            var delay = TimeSpan.FromMilliseconds(250);
            InfoCard.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)) { BeginTime = delay });
            if (InfoCard.RenderTransform is TranslateTransform shift)
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(850)) { BeginTime = delay, EasingFunction = Anim.OutQuart });
        }
        else
        {
            // Смена версии: текст в карточке мягко «мигает».
            InfoBody.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)));
        }
    }

    // ───────────────────────── Фон: картинка обновления ─────────────────────────

    private void SetHeroImage(ImageSource? image)
    {
        var token = ++_heroSwapToken;
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fadeOut.Completed += (_, _) =>
        {
            if (token != _heroSwapToken) return; // за это время пришла другая картинка
            if (image is null)
            {
                HeroBg.Background = null;
            }
            else
            {
                // Плавное «наезжание» камеры: картинка чуть приближена и за 2 секунды встаёт на место.
                var zoom = new ScaleTransform(1.07, 1.07, 0.5, 0.5);
                HeroBg.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill, RelativeTransform = zoom };
                var settle = new DoubleAnimation(1.07, 1.0, TimeSpan.FromMilliseconds(2200)) { EasingFunction = Anim.OutCubic };
                zoom.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
                zoom.BeginAnimation(ScaleTransform.ScaleYProperty, settle);
            }
            HeroBg.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(image is null ? 0 : _heroOpacity, TimeSpan.FromMilliseconds(600)));
        };
        HeroBg.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    // ───────────────────────── Подсказка прокрутки ─────────────────────────

    // Точка в «колёсике мыши» плавно ездит вниз-вверх. Анимация идёт только пока главная видна.
    private void StartScrollHint()
    {
        var loop = TimeSpan.FromMilliseconds(950);
        if (ScrollDot.RenderTransform is TranslateTransform shift)
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 9, loop)
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        ScrollDot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.3, loop)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    private void StopScrollHint()
    {
        if (ScrollDot.RenderTransform is TranslateTransform shift)
            shift.BeginAnimation(TranslateTransform.YProperty, null);
        ScrollDot.BeginAnimation(UIElement.OpacityProperty, null);
    }

    // ───────────────────────── Индикатор страниц ─────────────────────────

    private void AnimateDots(bool versionsActive)
    {
        var duration = TimeSpan.FromMilliseconds(320);
        DotHome.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(versionsActive ? 6 : 22, duration) { EasingFunction = Anim.OutQuart });
        DotVersions.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(versionsActive ? 22 : 6, duration) { EasingFunction = Anim.OutQuart });
        if (DotHome.Background is SolidColorBrush home)
            home.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(versionsActive ? DotIdle : DotActive, duration));
        if (DotVersions.Background is SolidColorBrush versions)
            versions.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(versionsActive ? DotActive : DotIdle, duration));
    }

    private void SetDotsVisible(bool visible)
    {
        PageDots.IsHitTestVisible = visible;
        PageDots.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(250)));
    }
}
