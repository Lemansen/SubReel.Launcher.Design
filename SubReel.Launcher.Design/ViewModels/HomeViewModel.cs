using System.Globalization;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

namespace SubReel.Launcher.Design.ViewModels;

/// <summary>Главная: чип над заголовком, карточка об обновлении справа и картинка обновления на фоне.</summary>
public sealed class HomeViewModel : ObservableObject
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly VersionStore _store;
    private readonly IImageService _images;
    private readonly ILogger<HomeViewModel> _logger;
    private int _heroToken;
    private bool _heroMissing;

    private string _heroChipText = "Minecraft Java Edition";
    private string _infoTitle = "Minecraft";
    private string _infoLabel = "ОБНОВЛЕНИЕ";
    private string _infoMeta = "";
    private string _infoSummary = "";
    private string _infoFooter = "";
    private bool _isInfoCardVisible;
    private ImageSource? _heroImage;

    public HomeViewModel(VersionStore store, IImageService images, ILogger<HomeViewModel> logger)
    {
        _store = store;
        _images = images;
        _logger = logger;
        store.Changed += (_, _) => UpdateHeroInfo();
        store.VersionSelected += (_, _) => RefreshHeroImageAsync().FireAndForget(_logger, "обновление картинки главной");
    }

    /// <summary>Тексты карточки обновились: окно проигрывает появление (в первый раз) или «мигание» текста.</summary>
    public event EventHandler? InfoUpdated;

    public string HeroChipText { get => _heroChipText; private set => SetProperty(ref _heroChipText, value); }
    public string InfoTitle { get => _infoTitle; private set => SetProperty(ref _infoTitle, value); }
    public string InfoLabel { get => _infoLabel; private set => SetProperty(ref _infoLabel, value); }
    public string InfoMeta { get => _infoMeta; private set => SetProperty(ref _infoMeta, value); }
    public string InfoSummary { get => _infoSummary; private set { if (SetProperty(ref _infoSummary, value)) OnPropertyChanged(nameof(HasInfoSummary)); } }
    public bool HasInfoSummary => !string.IsNullOrWhiteSpace(_infoSummary);
    public string InfoFooter { get => _infoFooter; private set => SetProperty(ref _infoFooter, value); }
    public bool IsInfoCardVisible { get => _isInfoCardVisible; private set => SetProperty(ref _isInfoCardVisible, value); }

    /// <summary>У версии есть картинка, но загрузить её не удалось (нет сети): показана заглушка, при появлении сети картинку нужно догрузить.</summary>
    public bool HeroImageMissing => _heroMissing;

    /// <summary>Картинка обновления выбранной версии (или пиксельная заглушка). null — фона нет.</summary>
    public ImageSource? HeroImage { get => _heroImage; private set => SetProperty(ref _heroImage, value); }

    /// <summary>Чип над заголовком и карточка справа: версия, дата выхода, краткое описание.</summary>
    private void UpdateHeroInfo()
    {
        var version = _store.SelectedVersion;
        if (string.IsNullOrEmpty(version))
        {
            HeroChipText = "Minecraft Java Edition";
            return;
        }

        // Пока список версий не загружен, показываем только чип: данные карточки ещё не настоящие.
        if (!_store.CatalogLoaded)
        {
            HeroChipText = $"Выбрана версия {version}";
            return;
        }

        var catalog = _store.Catalog;
        var isLatest = !catalog.IsOffline && version == catalog.LatestRelease;
        HeroChipText = isLatest ? $"Версия {version} уже в лаунчере!" : $"Выбрана версия {version}";

        var line = catalog.LineOf(version);
        var release = catalog.Find(version);
        InfoTitle = $"Minecraft {line?.Key ?? version}";

        // Над номером — название обновления, если оно известно («WILDERNESS BOUND»).
        InfoLabel = string.IsNullOrEmpty(line?.Title) ? "ОБНОВЛЕНИЕ" : line.Title.ToUpperInvariant();

        var meta = new List<string> { version };
        if (!catalog.IsOffline && release is { ReleaseTime: var time } && time > DateTimeOffset.MinValue)
            meta.Add("вышла " + time.ToLocalTime().ToString("d MMM yyyy", Ru));
        InfoMeta = string.Join(" · ", meta);

        var summary = catalog.SummaryFor(version);
        InfoSummary = string.IsNullOrWhiteSpace(summary) ? ""
            : summary.Length > 180 ? summary[..180].TrimEnd() + "…" : summary;

        var footer = line is null ? "" : $"{line.Versions.Count} {TextHelpers.Plural(line.Versions.Count, "версия", "версии", "версий")} в линейке";
        if (isLatest) footer = footer.Length > 0 ? footer + " · последний релиз" : "Последний релиз";
        InfoFooter = footer;

        IsInfoCardVisible = true;
        InfoUpdated?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshHeroImageAsync()
    {
        var token = ++_heroToken;
        var version = _store.SelectedVersion;
        var url = string.IsNullOrEmpty(version) ? null : _store.Catalog.ImageFor(version);
        ImageSource? image = url is null ? null : await _images.LoadAsync(url, 2200);
        if (token != _heroToken) return; // за это время выбрали другую версию
        _heroMissing = url is not null && image is null;

        // Нет картинки (старая версия, нет сети, сбой загрузки) — вместо чёрного фона пиксельная заглушка.
        if (image is null && !string.IsNullOrEmpty(version))
            image = PlaceholderArt.Hero(_store.Catalog.LineOf(version)?.Key ?? version);
        HeroImage = image;
    }
}
