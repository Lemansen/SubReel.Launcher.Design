using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SubReel.Launcher.Application.News;
using SubReel.Launcher.Application.Versions;
using SubReel.Launcher.Domain.News;

namespace SubReel.Launcher.Infrastructure.News;

/// <summary>Minecraft: настоящие данные Mojang — заметки об обновлениях Java Edition (картинка, название, краткое описание).</summary>
public sealed class MojangNewsSource : INewsSource
{
    private static readonly string[] Urls =
    {
        "https://launchercontent.mojang.com/v2/javaPatchNotes.json",
        "https://launchercontent.mojang.com/javaPatchNotes.json",
    };
    private const string ContentBase = "https://launchercontent.mojang.com";
    private const int MaxItems = 24;

    private readonly HttpClient _http;
    private readonly VersionStore _versions;
    private readonly ILogger<MojangNewsSource> _logger;

    public MojangNewsSource(HttpClient http, VersionStore versions, ILogger<MojangNewsSource> logger)
    {
        _http = http;
        _versions = versions;
        _logger = logger;
    }

    public string Name => "mojang";
    public bool IsConfigured => true;
    public IReadOnlyCollection<NewsCategory> Categories { get; } = new[] { NewsCategory.Minecraft };

    public async Task<IReadOnlyList<NewsItem>> LoadAsync(CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in Urls)
        {
            try
            {
                var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
                var items = Parse(json);
                if (items.Count > 0) return items;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Не удалось загрузить заметки об обновлениях с {Url}.", url);
                last = ex;
            }
        }
        // Оба адреса не ответили — это сбой источника. Пустой, но валидный ответ сбоем не считается.
        if (last is not null) ExceptionDispatchInfo.Capture(last).Throw();
        return Array.Empty<NewsItem>();
    }

    internal List<NewsItem> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return new List<NewsItem>();

        var catalog = _versions.Catalog;
        var items = new List<NewsItem>();
        foreach (var entry in entries.EnumerateArray())
        {
            var title = Str(entry, "title");
            var version = Str(entry, "version");
            if (string.IsNullOrWhiteSpace(title)) title = string.IsNullOrWhiteSpace(version) ? null : $"Minecraft {version}";
            if (string.IsNullOrWhiteSpace(title)) continue;

            string? image = null;
            if (entry.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object)
                image = Str(img, "url");

            // Дата: из самой записи, иначе — дата выхода релиза из каталога версий.
            DateTimeOffset? date = null;
            if (DateTimeOffset.TryParse(Str(entry, "date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) date = parsed;
            else if (version is not null && catalog.Find(version) is { ReleaseTime: var release } && release > DateTimeOffset.MinValue) date = release;

            var summary = Str(entry, "shortText");
            items.Add(new NewsItem
            {
                Id = "mc:" + (Str(entry, "id") ?? version ?? title),
                Category = NewsCategory.Minecraft,
                Title = title.Trim(),
                Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim(),
                ImageUrl = string.IsNullOrWhiteSpace(image) ? null : Absolute(image),
                PublishedAt = date,
                Source = Name,
            });
        }

        return items
            .OrderByDescending(i => i.PublishedAt ?? DateTimeOffset.MinValue)
            .Take(MaxItems)
            .ToList();
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Absolute(string url) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url
        : ContentBase + (url.StartsWith('/') ? url : "/" + url);
}
