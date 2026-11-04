using System.Net;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Mods;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Mods.Modrinth;

namespace SubReel.Launcher.Mods.CurseForge;


/// <summary>
/// Каталог модов CurseForge (API v1, заголовок <c>x-api-key</c>). Определение файлов по SHA-1 и проверка обновлений по хэшам не поддерживаются
/// (CurseForge ищет по собственному отпечатку), поэтому мод ставится вручную из поиска, а не «подхватывается» из папки mods.
/// </summary>
public sealed class CurseForgeCatalog : IModCatalog
{
    public const string BaseAddress = "https://api.curseforge.com/v1/";

    private readonly HttpClient _http;
    private readonly ICurseForgeKey _key;
    private readonly ILogger<CurseForgeCatalog> _logger;

    public CurseForgeCatalog(HttpClient http, ICurseForgeKey key, ILogger<CurseForgeCatalog> logger)
    {
        _http = http;
        _key = key;
        _logger = logger;
    }

    public ModSource Source => ModSource.CurseForge;

    /// <summary>Файлы по идентификаторам (для импорта сборки). Файлы без адреса скачивания в ответ не попадают — их запретил раздавать автор.</summary>
    public async Task<IReadOnlyList<ModVersion>> GetFilesAsync(IReadOnlyList<long> fileIds, CancellationToken ct = default)
    {
        var result = new List<ModVersion>();
        foreach (var chunk in fileIds.Where(i => i > 0).Distinct().Chunk(100))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "mods/files")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { fileIds = chunk }), System.Text.Encoding.UTF8, "application/json"),
            };
            result.AddRange(CurseForgeParser.ParseFiles(await SendAsync(request, ct).ConfigureAwait(false)));
        }
        return result;
    }

    public async Task<ModSearchResult> SearchAsync(ModSearchQuery query, CancellationToken ct = default)
    {
        var loader = RequireLoader(query.Loader);
        if (!ModrinthParser.IsSafeVersionText(query.McVersion)) throw new ModCatalogException($"Недопустимая версия игры «{query.McVersion}».");
        var text = (query.Text ?? "").Trim();
        var url = $"mods/search?gameId={CurseForgeParser.GameIdMinecraft}&classId={CurseForgeParser.ClassIdMods}&gameVersion={Uri.EscapeDataString(query.McVersion)}&modLoaderType={loader}"
                  + $"&sortField={(text.Length == 0 ? 6 : 2)}&sortOrder=desc&pageSize={Math.Clamp(query.Limit, 1, 50)}&index={Math.Max(0, query.Offset)}"
                  + (text.Length == 0 ? "" : "&searchFilter=" + Uri.EscapeDataString(text.Length > 100 ? text[..100] : text));
        return CurseForgeParser.ParseSearch(await GetAsync(url, ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<ModVersion>> GetVersionsAsync(string projectId, string mcVersion, LoaderKind loader, CancellationToken ct = default)
    {
        if (!long.TryParse(projectId, out var id) || id <= 0) throw new ModCatalogException("Недопустимый идентификатор мода.");
        var type = RequireLoader(loader);
        var url = $"mods/{id}/files?gameVersion={Uri.EscapeDataString(mcVersion)}&modLoaderType={type}&pageSize=50";
        return CurseForgeParser.ParseFiles(await GetAsync(url, ct).ConfigureAwait(false));
    }

    public Task<ModVersion?> GetVersionAsync(string versionId, CancellationToken ct = default) =>
        Task.FromResult<ModVersion?>(null); // файл CurseForge без id мода не запросить; зависимости задаются проектом

    public async Task<IReadOnlyList<ModProject>> GetProjectsAsync(IReadOnlyList<string> projectIds, CancellationToken ct = default)
    {
        var ids = projectIds.Select(i => long.TryParse(i, out var n) && n > 0 ? n : (long?)null).OfType<long>().Distinct().ToList();
        if (ids.Count == 0) return Array.Empty<ModProject>();

        using var request = new HttpRequestMessage(HttpMethod.Post, "mods")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { modIds = ids }), System.Text.Encoding.UTF8, "application/json"),
        };
        return CurseForgeParser.ParseProjects(await SendAsync(request, ct).ConfigureAwait(false));
    }

    public Task<IReadOnlyDictionary<string, ModVersion>> IdentifyFilesAsync(IReadOnlyList<string> sha1Hashes, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, ModVersion>>(new Dictionary<string, ModVersion>());

    public Task<IReadOnlyDictionary<string, ModVersion>> FindLatestForFilesAsync(IReadOnlyList<string> sha1Hashes, string mcVersion, LoaderKind loader, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, ModVersion>>(new Dictionary<string, ModVersion>());

    private static int RequireLoader(LoaderKind kind) =>
        CurseForgeParser.LoaderType(kind) ?? throw new ModCatalogException("Моды нужны сборке с загрузчиком (Fabric, Quilt, Forge или NeoForge).");

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = _key.Get();
        if (string.IsNullOrEmpty(key))
            throw new ModCatalogException("Укажи ключ API CurseForge: Настройки → CurseForge.");
        request.Headers.TryAddWithoutValidation("x-api-key", key);

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ModCatalogException("CurseForge не принял ключ API. Проверь ключ в настройках.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ModCatalogException("CurseForge просит не торопиться: слишком много запросов. Подожди минуту и повтори.");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CurseForge ответил {Status} на {Url}.", (int)response.StatusCode, request.RequestUri);
                throw new ModCatalogException($"CurseForge не смог обработать запрос (код {(int)response.StatusCode}). Попробуй ещё раз позже.");
            }
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModCatalogException("CurseForge долго не отвечает. Проверь подключение к интернету.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Не удалось связаться с CurseForge.");
            throw new ModCatalogException("Не удалось связаться с CurseForge. Проверь подключение к интернету.", ex);
        }
    }
}
