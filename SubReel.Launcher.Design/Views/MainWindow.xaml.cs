using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SubReel.Launcher.Design.Views;

/// <summary>
/// Окно лаунчера (View). Здесь нет бизнес-логики и состояния приложения: оно живёт в <see cref="MainViewModel"/>.
/// Код окна делает только то, что принадлежит самому окну: анимации переходов (по изменениям состояния ViewModel),
/// перетаскивание/сворачивание окна, колесо мыши и клавиши.
/// По файлам: Navigation — реакция на переходы, Home — главная и фон, Versions — «Все версии» и страница версии,
/// Menus — меню профиля и список версий, Overlays — модальное окно, Misc — уведомление, прогресс, новости.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        WireViewModel();
    }

    /// <summary>Подписки на изменения состояния, по которым окно запускает анимации.</summary>
    private void WireViewModel()
    {
        _vm.Nav.Changed += (_, _) => ApplyNavigation();
        _vm.Home.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HomeViewModel.HeroImage)) SetHeroImage(_vm.Home.HeroImage); };
        _vm.Home.InfoUpdated += (_, _) => ShowInfoCard();
        _vm.Versions.FilterApplied += (_, _) => VersionsScroll.ScrollToTop();
        _vm.Versions.Page.TextChanged += (_, _) => BlinkPageTexts();
        _vm.Toast.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ToastViewModel.IsVisible)) ApplyToast(_vm.Toast.IsVisible); };
        _vm.Launch.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LaunchViewModel.IsProgressExpanded)) ApplyProgressPanel(_vm.Launch.IsProgressExpanded); };
        _vm.Profile.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProfileViewModel.IsAddAccountOpen) && _vm.Profile.IsAddAccountOpen) FocusAddAccountBox(); };
        _vm.News.PropertyChanged += OnNewsChanged;
        _vm.News.ScrollToTopRequested += (_, _) => NewsScroll.ScrollToTop();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Две неподвижные панели появляются мягко: первое впечатление спокойнее.
        HeaderPanel.Opacity = 0;
        FooterPanel.Opacity = 0;
        HeaderPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = Anim.OutCubic });
        FooterPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520)) { BeginTime = TimeSpan.FromMilliseconds(100), EasingFunction = Anim.OutCubic });

        PlayHeroIntro();

        // Версии, картинки обновлений и первый запуск (сеть и диск не должны ронять лаунчер — перехват внутри).
        await _vm.InitializeAsync();
    }

    // Окно без рамки скруглено (как .app-shell, radius 32); в развёрнутом виде углы прямые.
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        LeaveFullScreenIfStateChanged();
        var radius = WindowState == WindowState.Maximized ? 0 : 32;
        WindowFrame.CornerRadius = new CornerRadius(radius);
        var inner = new CornerRadius(Math.Max(0, radius - 1));
        HeroBg.CornerRadius = inner;
        HeroShade.CornerRadius = inner;
        HeroShadeBottom.CornerRadius = inner;
        GlowBg.CornerRadius = inner;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_dropdownOpen) PositionVersionDropdown();
    }

    // ───────────────────────── Ввод ─────────────────────────

    // Колесо вниз на главной → «Все версии»; колесо вверх у начала списка → обратно.
    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var nav = _vm.Nav;
        if (!nav.CanSwitchModeByWheel) return;
        var now = DateTime.UtcNow;
        if ((now - _lastModeSwitchAt).TotalMilliseconds < 600) return;

        if (!nav.IsVersionsOpen)
        {
            if (e.Delta < 0 && nav.Page == MainPage.Home && nav.EnterVersions()) e.Handled = true;
        }
        else if (e.Delta > 0 && VersionsScroll.VerticalOffset <= 0.5 && (now - _lastVersionsScrollAt).TotalMilliseconds > 350)
        {
            nav.ExitVersions();
            e.Handled = true;
        }
    }

    private void VersionsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0) _lastVersionsScrollAt = DateTime.UtcNow;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape) return;

        // Первый Esc в поиске очищает запрос, второй — выходит из «Все версии».
        if (_vm.Nav.IsVersionsOpen && !_vm.Nav.IsVersionPageOpen && VersionSearchBox.IsKeyboardFocused && VersionSearchBox.Text.Length > 0)
        {
            _vm.Versions.ClearSearchCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (_vm.GoBack()) e.Handled = true;
    }

    private void Header_DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // Не начинаем перетаскивание, если курсор над кнопкой внутри шапки.
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not System.Windows.Controls.Primitives.ButtonBase)
            current = VisualTreeHelper.GetParent(current);

        if (current is null)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
