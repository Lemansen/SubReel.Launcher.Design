using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SubReel.Launcher.Design.Views;

// Реакция окна на переходы: NavigationService говорит «что открыто», а здесь сравнивается с тем, что уже показано, и запускаются нужные анимации.
public partial class MainWindow
{
    // То, что сейчас показано на экране (последнее применённое состояние).
    private MainPage _page = MainPage.Home;
    private OverlayViewModel? _overlay;
    private bool _profileMenuOpen;
    private bool _dropdownOpen;
    private bool _versionsOpen;
    private bool _pageOpen;
    private bool _newsOpen;
    private bool _buildsOpen;
    private bool _chromeHidden;
    private bool _dotsVisible = true;
    private bool _dotsVersionsActive;
    private double _heroOpacity = 1;

    private DateTime _lastModeSwitchAt = DateTime.MinValue;
    private DateTime _lastVersionsScrollAt = DateTime.MinValue;

    private void ApplyNavigation()
    {
        var nav = _vm.Nav;

        // Страницы под шапкой: главная, «Сообщество», Hub.
        if (nav.Page != _page)
        {
            _page = nav.Page;
            HomePanel.Visibility = _page == MainPage.Home ? Visibility.Visible : Visibility.Collapsed;
            CommunityPanel.Visibility = _page == MainPage.Community ? Visibility.Visible : Visibility.Collapsed;
            HubPanel.Visibility = _page == MainPage.Hub ? Visibility.Visible : Visibility.Collapsed;
            if (_page == MainPage.Hub) PlayHubIntro();
        }

        // Модальное окно (в том числе замена одного другим — анимация проигрывается заново).
        if (!ReferenceEquals(nav.Overlay, _overlay))
        {
            _overlay = nav.Overlay;
            if (_overlay is null) HideOverlay(); else ShowOverlay();
        }

        // Меню.
        if (nav.IsProfileMenuOpen != _profileMenuOpen)
        {
            _profileMenuOpen = nav.IsProfileMenuOpen;
            if (_profileMenuOpen) OpenProfileMenuAnimated(); else CloseProfileMenuAnimated();
        }
        if (nav.IsVersionDropdownOpen != _dropdownOpen)
        {
            _dropdownOpen = nav.IsVersionDropdownOpen;
            if (_dropdownOpen) OpenVersionDropdownAnimated(); else CloseVersionDropdownAnimated();
        }

        // «Все версии» и страница версии. Закрытие страницы идёт раньше выхода из списка: при выходе она просто гаснет вместе с ним.
        var pageWasOpen = _pageOpen;
        if (pageWasOpen && !nav.IsVersionPageOpen)
        {
            _pageOpen = false;
            _lastModeSwitchAt = DateTime.UtcNow;
            ClosePageAnimated(backToList: nav.IsVersionsOpen);
        }
        if (nav.IsVersionsOpen != _versionsOpen)
        {
            _versionsOpen = nav.IsVersionsOpen;
            _lastModeSwitchAt = DateTime.UtcNow;
            if (_versionsOpen) EnterVersionsAnimated(); else ExitVersionsAnimated();
        }
        if (!pageWasOpen && nav.IsVersionPageOpen)
        {
            _pageOpen = true;
            _lastModeSwitchAt = DateTime.UtcNow;
            OpenPageAnimated();
        }

        // Новости.
        if (nav.IsNewsOpen != _newsOpen)
        {
            _newsOpen = nav.IsNewsOpen;
            if (_newsOpen) OpenNewsAnimated(); else CloseNewsAnimated();
        }

        // Создание сборки.
        if (nav.IsBuildsOpen != _buildsOpen)
        {
            _buildsOpen = nav.IsBuildsOpen;
            if (_buildsOpen) OpenBuildsAnimated(); else CloseBuildsAnimated();
        }

        ApplyChrome();
    }

    /// <summary>Шапка, подвал, текст главной, фон и индикатор страниц следуют за состоянием.</summary>
    private void ApplyChrome()
    {
        var nav = _vm.Nav;

        if (nav.IsChromeHidden != _chromeHidden)
        {
            _chromeHidden = nav.IsChromeHidden;
            AnimateDisplacement(_chromeHidden);
            AnimateContent(_chromeHidden);
        }
        if (nav.HeroOpacity != _heroOpacity) SetHeroOpacity(nav.HeroOpacity);
        if (nav.AreDotsVisible != _dotsVisible)
        {
            _dotsVisible = nav.AreDotsVisible;
            SetDotsVisible(_dotsVisible);
        }
        if (nav.IsVersionsOpen != _dotsVersionsActive)
        {
            _dotsVersionsActive = nav.IsVersionsOpen;
            AnimateDots(_dotsVersionsActive);
        }
    }

    // .navbar.displaced-top / .bottom-bar.displaced-bottom: translateY(-130 / 160) + opacity 0, 0.5s easeOutQuart
    private void AnimateDisplacement(bool hide)
    {
        var duration = TimeSpan.FromMilliseconds(500);
        var fade = TimeSpan.FromMilliseconds(300);
        if (HeaderPanel.RenderTransform is TranslateTransform headerTransform)
            headerTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(hide ? -130 : 0, duration) { EasingFunction = Anim.OutQuart });
        if (FooterPanel.RenderTransform is TranslateTransform footerTransform)
            footerTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(hide ? 160 : 0, duration) { EasingFunction = Anim.OutQuart });
        HeaderPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(hide ? 0 : 1, fade));
        FooterPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(hide ? 0 : 1, fade));
    }

    // .content.fade-out-content: opacity 0 + scale(0.98)
    private void AnimateContent(bool hide)
    {
        ContentArea.IsHitTestVisible = !hide; // .fade-out-content { pointer-events: none }
        if (hide) StopScrollHint(); else StartScrollHint();
        ContentArea.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(hide ? 0 : 1, TimeSpan.FromMilliseconds(300)));
        if (ContentArea.RenderTransform is ScaleTransform scale)
        {
            var anim = new DoubleAnimation(hide ? 0.98 : 1, TimeSpan.FromMilliseconds(400));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    private void SetHeroOpacity(double value)
    {
        _heroOpacity = value;
        if (HeroBg.Background is not null)
            HeroBg.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(400)));
    }
}
