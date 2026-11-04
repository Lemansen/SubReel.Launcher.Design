using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Mods;
using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Mods.Modrinth;

/// <summary>
/// Каталог модов Modrinth (API v2, без ключа). Правила сервиса: обязательный уникальный User-Agent (задаёт регистрация клиента) и лимит около 300 запросов в минуту —
/// поэтому поиск вызывается не на каждый символ (задержка — в интерфейсе), а зависимости и названия запрашиваются пачками.
/// Ошибки сети и сервиса превращаются в <see cref="ModCatalogException"/> с понятным текстом; отмена остаётся отменой.
/// </summary>
public sealed class ModrinthCatalog : IModCatalog
{
    public const string BaseAddress = "https://api.modrinth.com/v2/";

    public ModSource Source => ModSource.Modrinth;

    private const int HashBatchSize = 200;
    private const int ProjectBatchSize = 100;

    private readonly HttpClient _http;
    private readonly ILogger<ModrinthCatalog> _logger;

    public ModrinthCatalog(HttpClient http, ILogger<ModrinthCatalog> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Загрузчики Modrinth для сборки. Quilt загружает и моды Fabric, поэтому подходят оба.</summary>
    public static IReadOnlyList<string> LoadersFor(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => new[] { "fabric" },
        LoaderKind.Quilt => new[] { "quilt", "fabric" },
        LoaderKind.Forge => new[] { "forge" },
        LoaderKind.NeoForge => new[] { "neoforge" },
        _ => Array.Empty<string>(),
    };

    public async Task<ModSearchResult> SearchAsync(ModSearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var loaders = RequireLoaders(query.Loader);
        if (!ModrinthParser.IsSafeVersionText(query.McVersion))
            throw new ModCatalogException($"Недопустимая версия игры «{query.McVersion}».");

        // Внешний массив — «И», внутренний — «ИЛИ»: мод для игры этой версии, на одном из загрузчиков, не только серверный.
        var facets = new[]
        {
            new[] { "project_type:mod" },
            new[] { $"versions:{query.McVersion}" },
            loaders.Select(l => $"categories:{l}").ToArray(),
            new[] { "client_side:required", "client_side:optional", "client_side:unknown" },
        };

        var text = (query.Text ?? "").Trim();
        var url = new StringBuilder("search?index=")
            .Append(text.Length == 0 ? "downloads" : "relevance")
            .Append("&limit=").Append(Math.Clamp(query.Limit, 1, 50))
            .Append("&offset=").Append(Math.Max(0, query.Offset))
            .Append("&facets=").Append(Uri.EscapeDataString(JsonSerializer.Serialize(facets)));
        if (text.Length > 0) url.Append("&query=").Append(Uri.EscapeDataString(text.Length > 100 ? text[..100] : text));

        return ModrinthParser.ParseSearch((await SendAsync(HttpMethod.Get, url.ToString(), null, ct).ConfigureAwait(false))!);
    }

    public async Task<IReadOnlyList<ModVersion>> GetVersionsAsync(string projectId, string mcVersion, LoaderKind loader, CancellationToken ct = default)
    {
        if (!ModrinthParser.IsSafeId(projectId)) throw new ModCatalogException("Недопустимый идентификатор мода.");
        var loaders = RequireLoaders(loader);

        var url = $"project/{projectId}/version?loaders={Uri.EscapeDataString(JsonSerializer.Serialize(loaders))}&game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { mcVersion }))}";
        return ModrinthParser.ParseVersions((await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false))!);
    }

    public async Task<ModVersion?> GetVersionAsync(string versionId, CancellationToken ct = default)
    {
        if (!ModrinthParser.IsSafeId(versionId)) throw new ModCatalogException("Недопустимый идентификатор версии.");
        return ModrinthParser.ParseVersion(await SendAsync(HttpMethod.Get, $"version/{versionId}", null, ct, allowNotFound: true).ConfigureAwait(false) ?? "null");
    }

    public async Task<IReadOnlyList<ModProject>> GetProjectsAsync(IReadOnlyList<string> projectIds, CancellationToken ct = default)
    {
        var ids = projectIds.Where(ModrinthParser.IsSafeId).Distinct(StringComparer.Ordinal).ToList();
        var result = new List<ModProject>();
        foreach (var chunk in ids.Chunk(ProjectBatchSize))
        {
            var url = "projects?ids=" + Uri.EscapeDataString(JsonSerializer.Serialize(chunk));
            result.AddRange(ModrinthParser.ParseProjects((await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false))!));
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, ModVersion>> IdentifyFilesAsync(IReadOnlyList<string> sha1Hashes, CancellationToken ct = default)
    {
        var map = new Dictionary<string, ModVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in NormalizeHashes(sha1Hashes).Chunk(HashBatchSize))
        {
            var body = JsonSerializer.Serialize(new { hashes = chunk, algorithm = "sha1" });
            foreach (var pair in ModrinthParser.ParseHashMap((await SendAsync(HttpMethod.Post, "version_files", body, ct).ConfigureAwait(false))!))
                map[pair.Key] = pair.Value;
        }
        return map;
    }

    public async Task<IReadOnlyDictionary<string, ModVersion>> FindLatestForFilesAsync(IReadOnlyList<string> sha1Hashes, string mcVersion, LoaderKind loader, CancellationToken ct = default)
    {
        var loaders = RequireLoaders(loader);
        var map = new Dictionary<string, ModVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in NormalizeHashes(sha1Hashes).Chunk(HashBatchSize))
        {
            var body = JsonSerializer.Serialize(new { hashes = chunk, algorithm = "sha1", loaders, game_versions = new[] { mcVersion } });
            foreach (var pair in ModrinthParser.ParseHashMap((await SendAsync(HttpMethod.Post, "version_files/update", body, ct).ConfigureAwait(false))!))
                map[pair.Key] = pair.Value;
        }
        return map;
    }

    private static IReadOnlyList<string> NormalizeHashes(IReadOnlyList<string> hashes) =>
        hashes.Where(h => h.Length == 40 && h.All(Uri.IsHexDigit)).Select(h => h.ToLowerInvariant()).Distinct().ToList();

    private static IReadOnlyList<string> RequireLoaders(LoaderKind kind)
    {
        var loaders = LoadersFor(kind);
        if (loaders.Count == 0) throw new ModCatalogException("Моды нужны сборке с загрузчиком (Fabric, Quilt, Forge или NeoForge).");
        return loaders;
    }

    /// <summary>Выполняет запрос и возвращает тело ответа. При <paramref name="allowNotFound"/> ответ 404 даёт null.</summary>
    private async Task<string?> SendAsync(HttpMethod method, string relativeUrl, string? jsonBody, CancellationToken ct, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, relativeUrl);
        if (jsonBody is not null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound) return null;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ModCatalogException("Modrinth просит не торопиться: слишком много запросов. Подожди минуту и повтори.");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Modrinth ответил {Status} на {Method} {Url}.", (int)response.StatusCode, method, relativeUrl);
                throw new ModCatalogException($"Modrinth не смог обработать запрос (код {(int)response.StatusCode}). Попробуй ещё раз позже.");
            }

            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModCatalogException("Modrinth долго не отвечает. Проверь подключение к интернету.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Не удалось связаться с Modrinth ({Url}).", relativeUrl);
            throw new ModCatalogException("Не удалось связаться с Modrinth. Проверь подключение к интернету.", ex);
        }
    }
}
