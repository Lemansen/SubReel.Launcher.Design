using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Minecraft.Layout;

namespace SubReel.Launcher.Minecraft.Installation;

/// <summary>
/// Память о файлах игры, у которых SHA-1 уже сошёлся: «путь → размер и время изменения», одним файлом <c>verified/files.json</c> на весь общий кэш
/// (файлы разных версий общие, поэтому и память одна). Пока файл не менялся, его не нужно хэшировать ни при запуске игры, ни при установке другой версии,
/// которой он тоже нужен: хватает проверки, что он на месте, а размер и время те же. Изменился размер или время — хэш считается заново.
/// Файл памяти можно удалить в любой момент: следующая проверка будет полной и запишет его заново.
/// </summary>
public sealed class VerifiedFilesCache
{
    private const int SchemaVersion = 1;

    private readonly GameFileLayout _layout;
    private readonly ILogger<VerifiedFilesCache> _logger;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private VerifiedFilesSnapshot? _snapshot;

    public VerifiedFilesCache(GameFileLayout layout, ILogger<VerifiedFilesCache> logger)
    {
        _layout = layout;
        _logger = logger;
    }

    /// <summary>Общая память: читается с диска один раз за работу лаунчера, дальше все пользуются одним объектом.</summary>
    public async Task<VerifiedFilesSnapshot> GetAsync(CancellationToken ct)
    {
        if (_snapshot is { } ready) return ready;

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _snapshot ??= await ReadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>Записывает память на диск, если с прошлой записи что-то изменилось.</summary>
    public async Task SaveAsync(CancellationToken ct)
    {
        var snapshot = _snapshot;
        if (snapshot is null || !snapshot.IsChanged) return;

        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var files = snapshot.ExportAndMarkSaved(); // изменения во время записи снова поднимут признак «изменено»
            var path = _layout.VerifiedFilesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, new CacheFile { Schema = SchemaVersion, Files = files }, cancellationToken: ct).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            snapshot.MarkChanged(); // не записалось — попробуем при следующем сохранении
            _logger.LogInformation(ex, "Не удалось записать память о проверенных файлах.");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<VerifiedFilesSnapshot> ReadAsync(CancellationToken ct)
    {
        var snapshot = new VerifiedFilesSnapshot(_layout.Root);
        try
        {
            var path = _layout.VerifiedFilesPath;
            if (!File.Exists(path)) return snapshot;

            await using var stream = File.OpenRead(path);
            var file = await JsonSerializer.DeserializeAsync<CacheFile>(stream, cancellationToken: ct).ConfigureAwait(false);
            if (file is { Schema: SchemaVersion, Files: not null }) snapshot.Load(file.Files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Повреждённая память — не беда: проверим всё полностью и запишем заново.
            _logger.LogInformation(ex, "Память о проверенных файлах не прочитана, проверка будет полной.");
            return new VerifiedFilesSnapshot(_layout.Root);
        }

        return snapshot;
    }

    private sealed class CacheFile
    {
        public int Schema { get; set; }
        public Dictionary<string, long[]>? Files { get; set; }
    }
}

/// <summary>Содержимое памяти. Потокобезопасно: файлы проверяются и скачиваются параллельно.</summary>
public sealed class VerifiedFilesSnapshot
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, (long Size, long Ticks)> _files = new(StringComparer.OrdinalIgnoreCase);
    private int _changed;

    public VerifiedFilesSnapshot(string root) => _root = root;

    public bool IsChanged => Volatile.Read(ref _changed) != 0;

    /// <summary>Файл точно такой же, как в момент успешной проверки хэша.</summary>
    public bool IsUnchanged(string path, FileInfo info) =>
        _files.TryGetValue(Key(path), out var stamp) && stamp.Size == info.Length && stamp.Ticks == info.LastWriteTimeUtc.Ticks;

    public void Remember(string path, FileInfo info)
    {
        var stamp = (info.Length, info.LastWriteTimeUtc.Ticks);
        if (_files.TryGetValue(Key(path), out var old) && old == stamp) return;

        _files[Key(path)] = stamp;
        MarkChanged();
    }

    /// <summary>Файл не прошёл проверку: прежняя запись о нём больше не верна.</summary>
    public void Forget(string path)
    {
        if (_files.TryRemove(Key(path), out _)) MarkChanged();
    }

    internal void MarkChanged() => Interlocked.Exchange(ref _changed, 1);

    internal void Load(Dictionary<string, long[]> files)
    {
        foreach (var (path, value) in files)
        {
            if (value is { Length: 2 }) _files[path] = (value[0], value[1]);
        }
    }

    internal Dictionary<string, long[]> ExportAndMarkSaved()
    {
        Interlocked.Exchange(ref _changed, 0);
        return _files.ToDictionary(p => p.Key, p => new[] { p.Value.Size, p.Value.Ticks }, StringComparer.OrdinalIgnoreCase);
    }

    // Пути хранятся относительно корня кэша: папку игры можно перенести, а файл остаётся небольшим.
    private string Key(string path) => Path.GetRelativePath(_root, path);
}
