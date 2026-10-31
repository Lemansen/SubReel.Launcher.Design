using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

namespace SubReel.Launcher.Design.ViewModels;

public enum NewsPane { List, Loading, State }

public static class NewsFormat
{
    public static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string CategoryName(NewsCategory category) => category switch
    {
        NewsCategory.SubReel => "Новости SubReel",
        NewsCategory.Minecraft => "Новости Minecraft",
        NewsCategory.Launcher => "Обновления лаунчера",
        NewsCategory.Servers => "Новости серверов",
        NewsCategory.Mods => "Моды и загрузчики",
        _ => "",
    };

    public static Color CategoryColor(NewsCategory category) => category switch
    {
        NewsCategory.SubReel => Color.FromRgb(0x78, 0xC8, 0xFF),
        NewsCategory.Minecraft => Color.FromRgb(0x6F, 0xCF, 0x97),
        NewsCategory.Launcher => Color.FromRgb(0xB0, 0x82, 0xFF),
        NewsCategory.Servers => Color.FromRgb(0xF5, 0xA2, 0x3C),
        _ => Color.FromRgb(0xF0, 0x7B, 0x4A),
    };

    public static string FormatDate(DateTimeOffset? date) =>
        date is { } d && d > DateTimeOffset.MinValue ? d.ToLocalTime().ToString("d MMMM yyyy", Ru) : "";

    public static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Карточка новости в списке (и большая, и маленькая — они отличаются только шаблоном).</summary>
public sealed class NewsCardViewModel : ObservableObject
{
    private ImageSource? _photo;

    public NewsCardViewModel(NewsItem item, Action<NewsCardViewModel> open)
    {
        Item = item;
        OpenCommand = new RelayCommand(() => open(this));

        var color = NewsFormat.CategoryColor(item.Category);
        CategoryName = NewsFormat.CategoryName(item.Category).ToUpperInvariant();
        CategoryBrush = NewsFormat.FrozenBrush(color);
        CategoryBorderBrush = NewsFormat.FrozenBrush(Color.FromArgb(0x88, color.R, color.G, color.B));
        DateText = NewsFormat.FormatDate(item.PublishedAt);
        FeaturedPlaceholder = PlaceholderArt.Hero(item.Id);
        CardPlaceholder = PlaceholderArt.Card(item.Id);
    }

    public NewsItem Item { get; }
    public string Title => Item.Title;
    public string? Summary => Item.Summary;
    public bool HasSummary => !string.IsNullOrWhiteSpace(Item.Summary);
    public string DateText { get; }
    public bool HasDate => DateText.Length > 0;
    public string CategoryName { get; }
    public Brush CategoryBrush { get; }
    public Brush CategoryBorderBrush { get; }
    public ImageSource FeaturedPlaceholder { get; }
    public ImageSource CardPlaceholder { get; }
    public ICommand OpenCommand { get; }

    public ImageSource? Photo { get => _photo; private set => SetProperty(ref _photo, value); }

    public async Task LoadPhotoAsync(IImageService images, int width, Func<bool> stillNeeded)
    {
        if (!TextHelpers.IsHttpUrl(Item.ImageUrl)) return;
        var image = await images.LoadAsync(Item.ImageUrl!, width);
        if (image is null || !stillNeeded()) return;
        Photo = image;
    }
}

/// <summary>Полная новость.</summary>
public sealed class NewsDetailViewModel : ObservableObject
{
    private ImageSource? _image;

    public NewsDetailViewModel(NewsItem item)
    {
        Item = item;
        var color = NewsFormat.CategoryColor(item.Category);
        CategoryName = NewsFormat.CategoryName(item.Category).ToUpperInvariant();
        CategoryBrush = NewsFormat.FrozenBrush(color);
        CategoryFill = NewsFormat.FrozenBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
        CategoryBorder = NewsFormat.FrozenBrush(Color.FromArgb(0x66, color.R, color.G, color.B));
        DateText = NewsFormat.FormatDate(item.PublishedAt);
        Body = !string.IsNullOrWhiteSpace(item.Body) ? item.Body!
            : !string.IsNullOrWhiteSpace(item.Summary) ? item.Summary!
            : "Для этой новости нет текста.";
        Placeholder = PlaceholderArt.Hero(item.Id);
    }

    public NewsItem Item { get; }
    public string Title => Item.Title;
    public string Body { get; }
    public string CategoryName { get; }
    public Brush CategoryBrush { get; }
    public Brush CategoryFill { get; }
    public Brush CategoryBorder { get; }
    public string DateText { get; }
    public bool HasDate => DateText.Length > 0;
    public bool CanOpenSite => TextHelpers.IsHttpUrl(Item.Url);
    public ImageSource Placeholder { get; }
    public ImageSource? Image { get => _image; set => SetProperty(ref _image, value); }
}

/// <summary>Экран новостей: главная новость, карточки, категории и состояния «загрузка / ошибка / пусто».</summary>
public sealed class NewsViewModel : ObservableObject, IDisposable
{
    private readonly INewsService _news;
    private readonly IBrowserService _browser;
    private readonly IImageService _images;
    private readonly IToastService _toast;
    private readonly NavigationService _nav;
    private readonly ILogger<NewsViewModel> _logger;

    private NewsCategory? _category;
    private NewsResult? _last;
    private int _loadToken;
    private CancellationTokenSource? _loadCts;
    private int _renderToken;
    private int _detailToken;

    private string _subtitle = "Что нового в SubReel и Minecraft";
    private NewsPane _pane = NewsPane.Loading;
    private bool _isBusy;
    private IReadOnlyList<ChipViewModel> _tabs = Array.Empty<ChipViewModel>();
    private string? _notice;
    private NewsCardViewModel? _featured;
    private IReadOnlyList<NewsCardViewModel> _more = Array.Empty<NewsCardViewModel>();
    private string? _footerNote;
    private string _stateTitle = "";
    private string _stateText = "";
    private string? _stateButtonText;
    private bool _isDetailOpen;
    private NewsDetailViewModel? _detail;

    public NewsViewModel(INewsService news, IBrowserService browser, IImageService images, IToastService toast, NavigationService nav, ILogger<NewsViewModel> logger)
    {
        _logger = logger;
        _news = news;
        _browser = browser;
        _images = images;
        _toast = toast;
        _nav = nav;

        RefreshCommand = new RelayCommand(() => LoadAsync(true).FireAndForget(_logger, "обновление новостей"));
        BackCommand = new RelayCommand(Close);
        CloseDetailCommand = new RelayCommand(() => IsDetailOpen = false);
        OpenSiteCommand = new RelayCommand(OpenSite);

        nav.NewsOpened += (_, _) =>
        {
            BuildTabs();
            ScrollToTopRequested?.Invoke(this, EventArgs.Empty);
            // Сохранённые новости показываем сразу, свежие подгружаем в фоне (не блокируя окно).
            if (_last is not null) Render(_last);
            LoadAsync(false).FireAndForget(_logger, "загрузка новостей");
        };
    }

    /// <summary>Список нужно прокрутить к началу (открыли экран или сменили категорию).</summary>
    public event EventHandler? ScrollToTopRequested;

    public ICommand RefreshCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand OpenSiteCommand { get; }

    public string Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }
    public NewsPane Pane { get => _pane; private set => SetProperty(ref _pane, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public IReadOnlyList<ChipViewModel> Tabs { get => _tabs; private set => SetProperty(ref _tabs, value); }

    public string? Notice { get => _notice; private set { if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool HasNotice => _notice is not null;
    public NewsCardViewModel? Featured { get => _featured; private set => SetProperty(ref _featured, value); }

    public IReadOnlyList<NewsCardViewModel> More
    {
        get => _more;
        private set { if (SetProperty(ref _more, value)) OnPropertyChanged(nameof(HasMore)); }
    }

    public bool HasMore => _more.Count > 0;
    public string? FooterNote { get => _footerNote; private set { if (SetProperty(ref _footerNote, value)) OnPropertyChanged(nameof(HasFooterNote)); } }
    public bool HasFooterNote => _footerNote is not null;

    public string StateTitle { get => _stateTitle; private set => SetProperty(ref _stateTitle, value); }
    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }
    public string? StateButtonText { get => _stateButtonText; private set { if (SetProperty(ref _stateButtonText, value)) OnPropertyChanged(nameof(HasStateButton)); } }
    public bool HasStateButton => _stateButtonText is not null;

    public bool IsDetailOpen { get => _isDetailOpen; set => SetProperty(ref _isDetailOpen, value); }
    public NewsDetailViewModel? Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    /// <summary>Отменяет незавершённую загрузку при закрытии лаунчера (контейнер освобождает singleton при остановке хоста).</summary>
    public void Dispose()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    public void Close()
    {
        IsDetailOpen = false;
        _nav.CloseNews();
    }

    // ───────────────────────── Категории ─────────────────────────

    private void BuildTabs()
    {
        var tabs = new List<ChipViewModel> { CreateTab("Все", null) };
        foreach (var category in Enum.GetValues<NewsCategory>())
            tabs.Add(CreateTab(NewsFormat.CategoryName(category), category));
        Tabs = tabs;
    }

    private ChipViewModel CreateTab(string title, NewsCategory? category) =>
        new(title, _category == category, new RelayCommand(() =>
        {
            _category = category;
            BuildTabs();
            ScrollToTopRequested?.Invoke(this, EventArgs.Empty);
            if (_last is not null) Render(_last);
        }));

    // ───────────────────────── Загрузка ─────────────────────────

    private async Task LoadAsync(bool force)
    {
        var token = ++_loadToken;

        // Новый запрос отменяет предыдущий (в том числе его сетевые обращения), а закрытие лаунчера — любой текущий.
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsBusy = true;
        if (_last is null || _last.Items.Count == 0) Pane = NewsPane.Loading;

        NewsResult result;
        try
        {
            result = await _news.LoadAsync(force, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Запрос заменён новым (тот сам снимет «занято») или лаунчер закрывается — результат не нужен.
            if (token == _loadToken) IsBusy = false;
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось загрузить новости.");
            result = new NewsResult { AnySourceConfigured = true, Failed = true };
        }
        if (token != _loadToken) return; // за это время запросили обновление ещё раз

        IsBusy = false;
        _last = result;
        Render(result);
    }

    // ───────────────────────── Отображение ─────────────────────────

    private void ShowState(string title, string text, string? buttonText)
    {
        StateTitle = title;
        StateText = text;
        StateButtonText = buttonText;
        Pane = NewsPane.State;
    }

    private void Render(NewsResult result)
    {
        Subtitle = result.FetchedAt is { } fetched
            ? "Обновлено " + fetched.ToLocalTime().ToString("d MMM, HH:mm", NewsFormat.Ru)
            : "Что нового в SubReel и Minecraft";

        if (!result.AnySourceConfigured)
        {
            ShowState("Источники новостей не подключены", "Новости появятся, когда будет подключён хотя бы один источник.", null);
            return;
        }
        if (result.Failed)
        {
            ShowState("Не удалось загрузить новости", "Проверь подключение к интернету и попробуй ещё раз. Сохранённых новостей пока нет.", "Повторить");
            return;
        }

        var items = _category is { } category ? result.Items.Where(i => i.Category == category).ToList() : result.Items.ToList();
        if (items.Count == 0)
        {
            if (_category is { } empty && !_news.HasSourceFor(empty))
                ShowState(NewsFormat.CategoryName(empty), "Источник новостей SubReel пока не подключён. Записи этой категории появятся здесь, когда он заработает.", null);
            else
                ShowState("Пока здесь нет новостей", _category is null ? "Источники ответили, но новостей в них нет." : "В этой категории пока ничего нет.", "Обновить");
            return;
        }

        BuildList(items, result);
        Pane = NewsPane.List;
    }

    private void BuildList(List<NewsItem> items, NewsResult result)
    {
        var token = ++_renderToken;
        Notice = BuildNotice(result);

        var featuredItem = items.FirstOrDefault(i => !string.IsNullOrEmpty(i.ImageUrl)) ?? items[0];
        var featured = new NewsCardViewModel(featuredItem, OpenDetail);
        var more = items.Where(i => i != featuredItem).Select(i => new NewsCardViewModel(i, OpenDetail)).ToList();
        Featured = featured;
        More = more;

        // На вкладке «Все» честно говорим, чего пока не хватает.
        FooterNote = _category is null && !_news.HasSourceFor(NewsCategory.SubReel)
            ? "Новости SubReel, лаунчера, серверов и модов появятся после подключения источника."
            : null;

        LoadImagesAsync(featured, more, token).FireAndForget(_logger, "картинки новостей");
    }

    /// <summary>Плашка над списком: показаны сохранённые новости (нет связи) или часть источников не ответила.</summary>
    private static string? BuildNotice(NewsResult result)
    {
        if (result.FailedSources.Count == 0) return null;
        return result.FromCache
            ? "Нет связи — показаны сохранённые новости" + (result.FetchedAt is { } at ? " от " + at.ToLocalTime().ToString("d MMM, HH:mm", NewsFormat.Ru) : "") + "."
            : "Не удалось обновить часть новостей. Остальные актуальны.";
    }

    /// <summary>Картинки подгружаются и плавно проявляются поверх заглушки; устаревший список их уже не получает.</summary>
    private async Task LoadImagesAsync(NewsCardViewModel featured, IReadOnlyList<NewsCardViewModel> more, int token)
    {
        bool Current() => token == _renderToken;
        var tasks = new List<Task> { featured.LoadPhotoAsync(_images, 1100, Current) };
        tasks.AddRange(more.Select(card => card.LoadPhotoAsync(_images, 560, Current)));
        await Task.WhenAll(tasks);
    }

    // ───────────────────────── Полная новость ─────────────────────────

    private void OpenDetail(NewsCardViewModel card)
    {
        if (IsDetailOpen) return;
        var token = ++_detailToken;
        var detail = new NewsDetailViewModel(card.Item);
        Detail = detail;
        IsDetailOpen = true;

        if (!TextHelpers.IsHttpUrl(card.Item.ImageUrl)) return;
        LoadDetailImageAsync(detail, card.Item.ImageUrl!, token).FireAndForget(_logger, "картинка новости");
    }

    private async Task LoadDetailImageAsync(NewsDetailViewModel detail, string url, int token)
    {
        var image = await _images.LoadAsync(url, 1100);
        if (image is null || token != _detailToken || !IsDetailOpen) return;
        detail.Image = image;
    }

    private void OpenSite()
    {
        if (Detail?.Item.Url is not { } url) return;
        if (!_browser.TryOpen(url)) _toast.Show("Не удалось открыть ссылку");
    }
}
