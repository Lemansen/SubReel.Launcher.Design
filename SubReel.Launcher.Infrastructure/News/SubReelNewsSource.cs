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

/// <summary>
/// Новости SubReel (проект, лаунчер, серверы, моды). Читает JSON-массив <see cref="NewsItem"/> по адресу из <see cref="NewsConfig"/>.
/// Пока адрес не задан, источник «не подключён»: данных нет, и ничего не придумывается.
/// </summary>
public sealed class SubReelNewsSource : INewsSource
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly HttpClient _http;
    private readonly string? _endpoint;

    public SubReelNewsSource(HttpClient http, IOptions<NewsOptions> options)
    {
        _http = http;
        _endpoint = options.Value.SubReelEndpoint;
    }

    public string Name => "subreel";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_endpoint);
    public IReadOnlyCollection<NewsCategory> Categories { get; } =
        new[] { NewsCategory.SubReel, NewsCategory.Launcher, NewsCategory.Servers, NewsCategory.Mods };

    public async Task<IReadOnlyList<NewsItem>> LoadAsync(CancellationToken ct)
    {
        if (!IsConfigured) return Array.Empty<NewsItem>();

        var json = await _http.GetStringAsync(_endpoint!, ct).ConfigureAwait(false);
        var items = JsonSerializer.Deserialize<List<NewsItem>>(json, Options) ?? new List<NewsItem>();
        var result = new List<NewsItem>();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Title) || !Categories.Contains(item.Category)) continue;
            if (string.IsNullOrWhiteSpace(item.Id)) item.Id = "subreel:" + item.Title;
            item.Source = Name;
            result.Add(item);
        }
        return result;
    }
}
