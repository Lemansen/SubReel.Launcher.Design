using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

namespace SubReel.Launcher.Design.ViewModels;

public static class VersionFormat
{
    public static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string LineCaption(VersionGroup line) =>
        string.IsNullOrEmpty(line.Title) ? $"Minecraft {line.Key}" : $"Minecraft {line.Key} · {line.Title}";
}

/// <summary>Кнопка конкретной версии на странице версии.</summary>
public sealed class PageChipViewModel : ObservableObject
{
    private bool _isSelected;

    public PageChipViewModel(string id, string? caption, bool isAccentCaption, Action<string> pick)
    {
        Id = id;
        Caption = caption;
        IsAccentCaption = isAccentCaption;
        PickCommand = new RelayCommand(() => pick(id));
    }

    public string Id { get; }
    public string? Caption { get; }
    public bool HasCaption => Caption is not null;
    public bool IsAccentCaption { get; }
    public ICommand PickCommand { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

/// <summary>Секция кнопок версий (у «Старых версий» — по линейкам, у остальных одна без заголовка).</summary>
public sealed class PageSectionViewModel
{
    public PageSectionViewModel(string? header, bool isFirst, IReadOnlyList<PageChipViewModel> chips)
    {
        Header = header;
        IsFirst = isFirst;
        Chips = chips;
    }

    public string? Header { get; }
    public bool HasHeader => Header is not null;
    public bool IsFirst { get; }
    public IReadOnlyList<PageChipViewModel> Chips { get; }
}

/// <summary>Миниатюра в галерее страницы версии.</summary>
public sealed class ThumbViewModel : ObservableObject
{
    private ImageSource? _image;
    private bool _isActive;

    public ThumbViewModel(string url, Action<string> show)
    {
        Url = url;
        ShowCommand = new RelayCommand(() => show(url));
    }

    public string Url { get; }
    public ICommand ShowCommand { get; }
    public ImageSource? Image { get => _image; set => SetProperty(ref _image, value); }
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
}

/// <summary>
/// Страница версии: клик по карточке в «Все версии» → картинка, описание, галерея и кнопка «Выбрать».
/// Картинки и описание берутся только из того, что уже есть в каталоге Mojang.
/// </summary>
public sealed class VersionPageViewModel : ObservableObject
{
    private const int MaxThumbs = 8;

    private readonly VersionStore _store;
    private readonly IImageService _images;
    private readonly NavigationService _nav;
    private readonly Action<string> _pickVersion;

    private VersionGroup? _group;
    private string _pageVersion = "";
    private string? _imageUrl;
    private int _imageToken;
    private List<PageChipViewModel> _chips = new();

    private string _label = "ОБНОВЛЕНИЕ";
    private string _title = "Minecraft";
    private string _meta = "";
    private string _summary = "";
    private bool _isSummaryMuted;
    private string _versionsLabel = "ВЕРСИЯ";
    private string _selectButtonText = "";
    private readonly ILogger _logger;
    private IReadOnlyList<PageSectionViewModel> _sections = Array.Empty<PageSectionViewModel>();
    private IReadOnlyList<ThumbViewModel> _thumbs = Array.Empty<ThumbViewModel>();
    private ImageSource? _image;
    private ImageSource? _placeholder;

    public VersionPageViewModel(VersionStore store, IImageService images, NavigationService nav, Action<string> pickVersion, ILogger logger)
    {
        _logger = logger;
        _store = store;
        _images = images;
        _nav = nav;
        _pickVersion = pickVersion;
        BackCommand = new RelayCommand(nav.CloseVersionPage);
        SelectCommand = new RelayCommand(() => _pickVersion(_pageVersion));
    }

    /// <summary>Описание или дата поменялись (выбрали другую версию на странице): окно мягко «мигает» текстом.</summary>
    public event EventHandler? TextChanged;

    public ICommand BackCommand { get; }
    public ICommand SelectCommand { get; }

    public string Label { get => _label; private set => SetProperty(ref _label, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Meta { get => _meta; private set => SetProperty(ref _meta, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public bool IsSummaryMuted { get => _isSummaryMuted; private set => SetProperty(ref _isSummaryMuted, value); }
    public string VersionsLabel { get => _versionsLabel; private set => SetProperty(ref _versionsLabel, value); }
    public string SelectButtonText { get => _selectButtonText; private set => SetProperty(ref _selectButtonText, value); }
    public IReadOnlyList<PageSectionViewModel> Sections { get => _sections; private set => SetProperty(ref _sections, value); }

    public IReadOnlyList<ThumbViewModel> Thumbs
    {
        get => _thumbs;
        private set { if (SetProperty(ref _thumbs, value)) OnPropertyChanged(nameof(HasThumbs)); }
    }

    /// <summary>Миниатюры показываются, только если у линейки больше одной картинки (ничего не выдумываем).</summary>
    public bool HasThumbs => _thumbs.Count > 1;

    /// <summary>Большая картинка. null, пока грузится (поверх лежит заглушка).</summary>
    public ImageSource? Image { get => _image; private set => SetProperty(ref _image, value); }
    public ImageSource? Placeholder { get => _placeholder; private set => SetProperty(ref _placeholder, value); }

    // ───────────────────────── Открытие ─────────────────────────

    public void Open(VersionGroup group)
    {
        _group = group;
        _pageVersion = group.Versions.Any(v => v.Id == _store.SelectedVersion) ? _store.SelectedVersion : group.Versions[0].Id;

        BuildGallery(group);
        BuildChips(group);
        FillTexts();
        UpdateSelection();
        SetImage(_store.Catalog.ImageFor(_pageVersion), first: true);
    }

    // ───────────────────────── Тексты ─────────────────────────

    private void FillTexts()
    {
        if (_group is not { } group) return;
        var catalog = _store.Catalog;
        var id = _pageVersion;
        var version = catalog.Find(id);

        Label = group.IsLegacy ? "СТАРЫЕ ВЕРСИИ"
            : string.IsNullOrEmpty(group.Title) ? "ОБНОВЛЕНИЕ" : group.Title.ToUpperInvariant();
        Title = group.IsLegacy ? "Старые версии" : $"Minecraft {group.Key}";

        var meta = new List<string> { id };
        if (!catalog.IsOffline && version is { ReleaseTime: var time } && time > DateTimeOffset.MinValue)
            meta.Add("вышла " + time.ToLocalTime().ToString("d MMM yyyy", VersionFormat.Ru));
        if (!catalog.IsOffline && id == catalog.LatestRelease) meta.Add("последний релиз");
        Meta = string.Join(" · ", meta);

        var summary = catalog.SummaryFor(id);
        if (string.IsNullOrWhiteSpace(summary))
        {
            Summary = group.IsLegacy
                ? "Старые релизы Minecraft Java. Выбери нужную версию ниже."
                : "Для этой версии нет описания обновления.";
            IsSummaryMuted = true;
        }
        else
        {
            Summary = summary;
            IsSummaryMuted = false;
        }
    }

    // ───────────────────────── Выбор версии на странице ─────────────────────────

    private void BuildChips(VersionGroup group)
    {
        _chips = new List<PageChipViewModel>();
        VersionsLabel = group.Versions.Count == 1 ? "ВЕРСИЯ" : "ВЫБЕРИ ВЕРСИЮ";

        var sections = new List<PageSectionViewModel>();
        if (group.IsLegacy)
        {
            foreach (var line in group.Lines!)
                sections.Add(CreateSection(VersionFormat.LineCaption(line), sections.Count == 0, line.Versions));
        }
        else
        {
            sections.Add(CreateSection(null, true, group.Versions));
        }
        Sections = sections;
    }

    private PageSectionViewModel CreateSection(string? header, bool isFirst, IEnumerable<McVersion> versions)
    {
        var chips = versions.Select(CreateChip).ToList();
        _chips.AddRange(chips);
        return new PageSectionViewModel(header, isFirst, chips);
    }

    private PageChipViewModel CreateChip(McVersion version)
    {
        var catalog = _store.Catalog;
        var isCurrent = version.Id == _store.SelectedVersion;
        var isLatest = !catalog.IsOffline && version.Id == catalog.LatestRelease;

        string? caption = null;
        if (isCurrent) caption = "сейчас выбрана";
        else if (isLatest) caption = "последняя";
        else if (!catalog.IsOffline && version.ReleaseTime > DateTimeOffset.MinValue)
            caption = version.ReleaseTime.ToLocalTime().ToString("d MMM yyyy", VersionFormat.Ru);

        return new PageChipViewModel(version.Id, caption, isCurrent || isLatest, SetVersion);
    }

    private void SetVersion(string id)
    {
        if (id == _pageVersion) return;
        _pageVersion = id;
        FillTexts();
        UpdateSelection();
        TextChanged?.Invoke(this, EventArgs.Empty);

        // Картинка меняется, только если у версии она своя.
        var url = _store.Catalog.ImageFor(id);
        if (url != _imageUrl) SetImage(url, first: false);
    }

    private void UpdateSelection()
    {
        foreach (var chip in _chips) chip.IsSelected = chip.Id == _pageVersion;
        SelectButtonText = _pageVersion == _store.SelectedVersion ? $"ОСТАВИТЬ {_pageVersion}" : $"ВЫБРАТЬ {_pageVersion}";
    }

    // ───────────────────────── Картинка и галерея ─────────────────────────

    private void BuildGallery(VersionGroup group)
    {
        // Все разные картинки обновлений линейки (у отдельных версий бывают свои).
        var urls = group.Versions.Select(v => v.ImageUrl).Prepend(group.ImageUrl)
            .Where(u => !string.IsNullOrEmpty(u)).Select(u => u!).Distinct().Take(MaxThumbs).ToList();

        var thumbs = urls.Select(url => new ThumbViewModel(url, shown => SetImage(shown, first: false))).ToList();
        Thumbs = thumbs;
        if (thumbs.Count > 1)
            foreach (var thumb in thumbs) LoadThumbAsync(thumb).FireAndForget(_logger, "миниатюра страницы версии");
    }

    private async Task LoadThumbAsync(ThumbViewModel thumb)
    {
        var image = await _images.LoadAsync(thumb.Url, 320);
        if (image is not null) thumb.Image = image;
    }

    private void SetImage(string? url, bool first)
    {
        _imageUrl = url;
        var token = ++_imageToken;
        foreach (var thumb in _thumbs) thumb.IsActive = thumb.Url == url;

        Placeholder = PlaceholderArt.Hero(_group?.Key ?? "");
        if (first) Image = null; // не показываем картинку прошлой страницы, пока грузится новая
        ApplyImageAsync(url, token).FireAndForget(_logger, "картинка страницы версии");
    }

    private async Task ApplyImageAsync(string? url, int token)
    {
        ImageSource? image = url is null ? null : await _images.LoadAsync(url, 1800);
        if (token != _imageToken) return; // за это время выбрали другую картинку
        Image = image;
    }
}
