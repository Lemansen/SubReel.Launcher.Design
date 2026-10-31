using System.Globalization;
using Microsoft.Extensions.Logging;
using System.Windows.Input;
using System.Windows.Media;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>Карточка линейки версий (1.21, 1.20, …) или сводная «Старые версии».</summary>
public sealed class VersionCardViewModel : ObservableObject
{
    private ImageSource? _photo;
    private bool _isSelected;
    private bool _isVisible = true;

    public VersionCardViewModel(VersionGroup group, bool isNew, Action<VersionGroup> open)
    {
        Group = group;
        IsNew = isNew;
        OpenCommand = new RelayCommand(() => open(group));
        Placeholder = PlaceholderArt.Card(group.Key);
        CountText = $"{group.Versions.Count} {TextHelpers.Plural(group.Versions.Count, "версия", "версии", "версий")}";
    }

    public VersionGroup Group { get; }
    public string DisplayKey => Group.IsLegacy ? "Старые версии" : Group.Key;
    public bool IsLegacy => Group.IsLegacy;

    /// <summary>Название обновления («Tricky Trials») или диапазон версий у сводной карточки.</summary>
    public string? Title => Group.Title;
    public bool HasTitle => !string.IsNullOrEmpty(Group.Title);
    public string CountText { get; }
    public bool IsNew { get; }
    public ICommand OpenCommand { get; }

    /// <summary>Пиксельная заглушка: у карточки всегда есть картинка, и без сети тоже.</summary>
    public ImageSource Placeholder { get; }

    /// <summary>Настоящая картинка обновления; плавно проявляется поверх заглушки.</summary>
    public ImageSource? Photo { get => _photo; private set => SetProperty(ref _photo, value); }

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    /// <summary>false — карточка скрыта поиском.</summary>
    public bool IsVisible { get => _isVisible; set => SetProperty(ref _isVisible, value); }

    public async Task LoadPhotoAsync(IImageService images, int width)
    {
        if (Group.ImageUrl is null) return;
        var image = await images.LoadAsync(Group.ImageUrl, width);
        if (image is not null) Photo = image;
    }
}

/// <summary>Секция карточек: заголовок («2026», «Старые версии») и под ним ряд карточек.</summary>
public sealed class VersionSectionViewModel : ObservableObject
{
    private bool _isVisible = true;

    public VersionSectionViewModel(string? header, bool isFirst)
    {
        Header = header;
        IsFirst = isFirst;
    }

    public string? Header { get; }
    public bool HasHeader => Header is not null;
    public bool IsFirst { get; }
    public List<VersionCardViewModel> Cards { get; } = new();

    /// <summary>Секция без видимых карточек (и её заголовок) скрывается.</summary>
    public bool IsVisible { get => _isVisible; private set => SetProperty(ref _isVisible, value); }

    public void RefreshVisibility() => IsVisible = Cards.Any(c => c.IsVisible);
}

/// <summary>Экран «Все версии»: карточки по годам, поиск, «Недавние» и страница выбранной линейки.</summary>
public sealed class VersionsViewModel : ObservableObject
{
    private readonly VersionStore _store;
    private readonly IImageService _images;
    private readonly NavigationService _nav;

    private IReadOnlyList<VersionSectionViewModel> _sections = Array.Empty<VersionSectionViewModel>();
    private List<VersionCardViewModel> _cards = new();
    private IReadOnlyList<ChipViewModel> _recent = Array.Empty<ChipViewModel>();
    private string _subtitle = "Выбери релиз Minecraft Java";
    private string _searchText = "";
    private bool _hasNoResults;

    public VersionsViewModel(VersionStore store, IImageService images, NavigationService nav, ILogger<VersionsViewModel> logger)
    {
        _store = store;
        _images = images;
        _nav = nav;

        Page = new VersionPageViewModel(store, images, nav, PickVersion, logger);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        SubmitSearchCommand = new RelayCommand(SubmitSearch);

        store.CatalogReady += (_, _) => BuildCards();
        store.CatalogRefreshed += (_, _) => BuildCards(); // сеть вернулась: даты, годы и картинки появляются без перезапуска
        store.Changed += (_, _) => UpdateCardSelection();
        nav.VersionsOpened += (_, _) => RefreshRecent();
        nav.VersionsClosed += (_, _) => SearchText = ""; // сбрасывает и фильтр
    }

    /// <summary>Фильтр применён — список стоит прокрутить к началу.</summary>
    public event EventHandler? FilterApplied;

    public VersionPageViewModel Page { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand SubmitSearchCommand { get; }

    public IReadOnlyList<VersionSectionViewModel> Sections { get => _sections; private set => SetProperty(ref _sections, value); }
    public IReadOnlyList<ChipViewModel> Recent { get => _recent; private set => SetProperty(ref _recent, value); }
    public string Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            OnPropertyChanged(nameof(HasSearchText));
            OnPropertyChanged(nameof(HasQuery));
            OnPropertyChanged(nameof(ShowRecent));
            ApplyFilter();
        }
    }

    public bool HasSearchText => _searchText.Length > 0;
    public bool HasQuery => _searchText.Trim().Length > 0;
    public bool HasNoResults { get => _hasNoResults; private set => SetProperty(ref _hasNoResults, value); }

    /// <summary>«Недавние» видны, пока поиск пуст.</summary>
    public bool ShowRecent => _recent.Count > 0 && !HasQuery;

    // ───────────────────────── Карточки ─────────────────────────

    private void BuildCards()
    {
        var catalog = _store.Catalog;
        Subtitle = catalog.IsOffline
            ? "Нет связи с серверами Mojang — показан встроенный список без картинок"
            : "Выбери релиз Minecraft Java";

        // Карточки лежат секциями: заголовок («2026», «2024», … «Старые версии») и под ним ряд карточек — как годы на minecraft.net.
        var sections = new List<VersionSectionViewModel>();
        var cards = new List<VersionCardViewModel>();
        VersionSectionViewModel? current = null;
        string? currentHeader = "\0"; // заведомо не совпадёт с настоящим заголовком
        for (var i = 0; i < catalog.Groups.Count; i++)
        {
            var group = catalog.Groups[i];
            var header = SectionHeader(catalog, group);
            if (current is null || header != currentHeader)
            {
                currentHeader = header;
                current = new VersionSectionViewModel(header, sections.Count == 0);
                sections.Add(current);
            }

            var card = new VersionCardViewModel(group, isNew: i == 0 && !catalog.IsOffline, OpenPage);
            current.Cards.Add(card);
            cards.Add(card);
        }

        _cards = cards;
        Sections = sections;
        UpdateCardSelection();
        RefreshRecent();
        ApplyFilter();
    }

    /// <summary>Заголовок секции: год первого релиза линейки или «Старые версии». Без сети годов нет (даты выдуманные).</summary>
    private static string? SectionHeader(VersionCatalog catalog, VersionGroup group)
    {
        if (catalog.IsOffline) return null;
        return group.IsLegacy ? "Старые версии" : group.FirstRelease.ToLocalTime().Year.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>У каких-то карточек картинка есть в каталоге, но не загрузилась (нет сети).</summary>
    public bool HasMissingPhotos => _cards.Any(c => c.Group.ImageUrl is not null && c.Photo is null);

    /// <summary>Подгружает картинки карточек (параллельность ограничивает IImageService).</summary>
    public Task LoadCardImagesAsync() =>
        Task.WhenAll(_cards.Where(c => c.Group.ImageUrl != null).Select(c => c.LoadPhotoAsync(_images, 640)).ToList());

    private void UpdateCardSelection()
    {
        var selectedKey = string.IsNullOrEmpty(_store.SelectedVersion) ? null : _store.Catalog.CardOf(_store.SelectedVersion)?.Key;
        foreach (var card in _cards) card.IsSelected = card.Group.Key == selectedKey;
    }

    // ───────────────────────── Поиск и «Недавние» ─────────────────────────

    private void ApplyFilter()
    {
        var query = _searchText.Trim();
        var hasQuery = query.Length > 0;

        var anyCard = false;
        foreach (var card in _cards)
        {
            var show = !hasQuery || MatchesQuery(card.Group, query);
            card.IsVisible = show;
            anyCard |= show;
        }
        foreach (var section in _sections) section.RefreshVisibility();

        HasNoResults = hasQuery && !anyCard;
        FilterApplied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Совпадение по номеру версии, линейке, названию обновления («Tricky Trials»); у «Старых версий» — ещё и по вошедшим линейкам.</summary>
    private static bool MatchesQuery(VersionGroup group, string query)
    {
        bool Has(string? text) => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        return Has(group.Key)
            || Has(group.Title)
            || group.Versions.Any(v => Has(v.Id))
            || (group.Lines?.Any(l => Has(l.Title)) ?? false)
            || (group.IsLegacy && Has("старые версии"));
    }

    // Enter при точном номере версии («1.20.4») выбирает её сразу, не открывая страницу линейки.
    private void SubmitSearch()
    {
        if (_store.Catalog.Find(_searchText.Trim()) is { } exact) PickVersion(exact.Id);
    }

    private void RefreshRecent()
    {
        var catalog = _store.Catalog;
        Recent = _store.RecentVersions
            .Where(id => catalog.Find(id) != null)
            .Select(id => new ChipViewModel(id, id == _store.SelectedVersion, new RelayCommand(() => PickVersion(id))))
            .ToList();
        OnPropertyChanged(nameof(ShowRecent));
    }

    // ───────────────────────── Действия ─────────────────────────

    private void OpenPage(VersionGroup group)
    {
        if (!_nav.IsVersionsOpen || _nav.IsVersionPageOpen) return;
        Page.Open(group);
        _nav.OpenVersionPage();
    }

    /// <summary>Выбрать версию и вернуться на главную.</summary>
    private void PickVersion(string id)
    {
        _store.Select(id);
        _nav.ExitVersions();
    }
}
