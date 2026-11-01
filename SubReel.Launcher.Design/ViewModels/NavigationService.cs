namespace SubReel.Launcher.Design.ViewModels;

public enum MainPage { Home, Community, Hub }

/// <summary>
/// Единственное место, где хранится «что сейчас открыто»: страница, экран «Все версии», страница версии, новости,
/// меню и модальное окно. Другие ViewModel-и просят его открыть/закрыть экран, а окно (View) подписано на <see cref="Changed"/>
/// и сравнивает старое состояние с новым — так анимации остаются в View, а правила переходов — здесь.
/// Каждый метод меняет состояние целиком и вызывает <see cref="Commit"/> один раз.
/// </summary>
public sealed class NavigationService : ObservableObject
{
    /// <summary>Состояние изменилось (после любого перехода).</summary>
    public event EventHandler? Changed;

    // Точечные события: ViewModel-и готовят данные до того, как окно начнёт анимацию.
    public event EventHandler? ProfileMenuOpened;
    public event EventHandler? VersionDropdownOpened;
    public event EventHandler? VersionsOpened;
    public event EventHandler? VersionsClosed;
    public event EventHandler? NewsOpened;
    public event EventHandler? BuildsOpened;

    public MainPage Page { get; private set; } = MainPage.Home;
    public bool IsVersionsOpen { get; private set; }
    public bool IsVersionPageOpen { get; private set; }
    public bool IsNewsOpen { get; private set; }
    public bool IsBuildsOpen { get; private set; }
    public bool IsProfileMenuOpen { get; private set; }
    public bool IsVersionDropdownOpen { get; private set; }
    public OverlayViewModel? Overlay { get; private set; }

    // ───────────────────────── Производные состояния ─────────────────────────

    public bool IsOverlayOpen => Overlay is not null;

    /// <summary>Шапка и подвал уезжают, а текст главной гаснет, пока открыт любой полноэкранный раздел.</summary>
    public bool IsChromeHidden => IsOverlayOpen || IsVersionsOpen || IsNewsOpen || IsBuildsOpen;

    /// <summary>Насколько яркой должна быть картинка обновления на фоне.</summary>
    public double HeroOpacity => IsVersionPageOpen ? 0.15 : IsVersionsOpen || IsNewsOpen || IsBuildsOpen ? 0.35 : 1;

    /// <summary>Индикатор «страниц» справа виден везде, кроме модальных окон и новостей.</summary>
    public bool AreDotsVisible => !IsOverlayOpen && !IsNewsOpen && !IsBuildsOpen;

    private bool TabsActive => !IsOverlayOpen && !IsNewsOpen && !IsBuildsOpen;
    public bool IsHomeTabActive => TabsActive && Page == MainPage.Home;
    public bool IsCommunityTabActive => TabsActive && Page == MainPage.Community;
    public bool IsHubTabActive => TabsActive && Page == MainPage.Hub;

    /// <summary>Колесо мыши переключает «Главная ↔ Все версии» только когда ничего поверх не открыто.</summary>
    public bool CanSwitchModeByWheel =>
        !(IsOverlayOpen || IsVersionPageOpen || IsProfileMenuOpen || IsVersionDropdownOpen || IsNewsOpen || IsBuildsOpen);

    // ───────────────────────── Страницы ─────────────────────────

    public void ShowPage(MainPage page)
    {
        if (page == Page && !IsOverlayOpen) return;
        IsProfileMenuOpen = false;
        IsVersionDropdownOpen = false;
        SetOverlay(null);
        Page = page;
        Commit();
    }

    // ───────────────────────── Все версии и страница версии ─────────────────────────

    public bool EnterVersions()
    {
        if (IsVersionsOpen || Page != MainPage.Home || IsNewsOpen || IsBuildsOpen || IsOverlayOpen) return false;
        IsVersionsOpen = true;
        VersionsOpened?.Invoke(this, EventArgs.Empty);
        Commit();
        return true;
    }

    /// <summary>Пункт «Все версии Minecraft» в выпадающем списке: закрывает список, возвращает на главную и открывает каталог.</summary>
    public void OpenAllVersions()
    {
        IsVersionDropdownOpen = false;
        Page = MainPage.Home;
        if (!IsVersionsOpen && !IsNewsOpen && !IsBuildsOpen && !IsOverlayOpen)
        {
            IsVersionsOpen = true;
            VersionsOpened?.Invoke(this, EventArgs.Empty);
        }
        Commit();
    }

    public void ExitVersions()
    {
        if (!IsVersionsOpen) return;
        IsVersionPageOpen = false; // страница гаснет вместе со списком
        IsVersionsOpen = false;
        VersionsClosed?.Invoke(this, EventArgs.Empty);
        Commit();
    }

    public bool OpenVersionPage()
    {
        if (IsVersionPageOpen || !IsVersionsOpen) return false;
        IsVersionPageOpen = true;
        Commit();
        return true;
    }

    public void CloseVersionPage()
    {
        if (!IsVersionPageOpen) return;
        IsVersionPageOpen = false;
        Commit();
    }

    // ───────────────────────── Новости ─────────────────────────

    public bool OpenNews()
    {
        if (IsNewsOpen || IsVersionsOpen || IsBuildsOpen || IsOverlayOpen) return false;
        IsProfileMenuOpen = false;
        IsVersionDropdownOpen = false;
        IsNewsOpen = true;
        NewsOpened?.Invoke(this, EventArgs.Empty);
        Commit();
        return true;
    }

    public void CloseNews()
    {
        if (!IsNewsOpen) return;
        IsNewsOpen = false;
        Commit();
    }

    // ───────────────────────── Создание сборки ─────────────────────────

    public bool OpenBuilds()
    {
        if (IsBuildsOpen || IsVersionsOpen || IsNewsOpen || IsOverlayOpen) return false;
        IsProfileMenuOpen = false;
        IsVersionDropdownOpen = false;
        IsBuildsOpen = true;
        BuildsOpened?.Invoke(this, EventArgs.Empty);
        Commit();
        return true;
    }

    public void CloseBuilds()
    {
        if (!IsBuildsOpen) return;
        IsBuildsOpen = false;
        Commit();
    }

    // ───────────────────────── Меню ─────────────────────────

    public void ToggleProfileMenu()
    {
        if (IsProfileMenuOpen) CloseProfileMenu(); else OpenProfileMenu();
    }

    public void OpenProfileMenu()
    {
        if (IsProfileMenuOpen) return;
        IsVersionDropdownOpen = false;
        IsProfileMenuOpen = true;
        ProfileMenuOpened?.Invoke(this, EventArgs.Empty);
        Commit();
    }

    public void CloseProfileMenu()
    {
        if (!IsProfileMenuOpen) return;
        IsProfileMenuOpen = false;
        Commit();
    }

    public void ToggleVersionDropdown()
    {
        if (IsVersionDropdownOpen) CloseVersionDropdown(); else OpenVersionDropdown();
    }

    public bool OpenVersionDropdown()
    {
        if (IsVersionDropdownOpen || IsVersionsOpen || IsNewsOpen || IsBuildsOpen || IsOverlayOpen) return false;
        IsProfileMenuOpen = false;
        IsVersionDropdownOpen = true;
        VersionDropdownOpened?.Invoke(this, EventArgs.Empty);
        Commit();
        return true;
    }

    public void CloseVersionDropdown()
    {
        if (!IsVersionDropdownOpen) return;
        IsVersionDropdownOpen = false;
        Commit();
    }

    // ───────────────────────── Модальное окно ─────────────────────────

    public void ShowOverlay(OverlayViewModel overlay)
    {
        IsProfileMenuOpen = false;
        SetOverlay(overlay);
        Commit();
    }

    public void CloseOverlay()
    {
        if (Overlay is null) return;
        SetOverlay(null);
        Commit();
    }

    /// <summary>Меняет модальное окно; прежнее, если оно IDisposable (вход Microsoft), освобождается — так отменяется незавершённый вход.</summary>
    private void SetOverlay(OverlayViewModel? next)
    {
        var previous = Overlay;
        Overlay = next;
        if (!ReferenceEquals(previous, next) && previous is IDisposable disposable) disposable.Dispose();
    }

    private void Commit()
    {
        OnPropertyChanged(string.Empty); // «изменились все свойства» — обновит привязки к Nav.*
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
