using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubReel.Launcher.Application.Instances;
using SubReel.Launcher.Domain.Instances;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Minecraft.Instances;

/// <summary>
/// Экземпляры на диске: <c>instances/&lt;id&gt;/instance.json</c> (описание, запись атомарная через <see cref="JsonFileStore"/>)
/// и <c>instances/&lt;id&gt;/minecraft</c> (папка игры). Каждый вызов чтения возвращает новые объекты, изменения в них
/// попадают на диск только через <see cref="UpdateAsync"/>. Файл от более новой версии лаунчера не читается и не перезаписывается.
/// </summary>
public sealed class JsonInstanceRepository : IInstanceRepository
{
    public const string DescriptionFileName = "instance.json";
    public const string GameDirectoryName = "minecraft";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppPaths _paths;
    private readonly JsonFileStore _store;
    private readonly ILogger<JsonInstanceRepository> _logger;
    private readonly SemaphoreSlim _mutation = new(1, 1);

    public JsonInstanceRepository(AppPaths paths, JsonFileStore store, ILogger<JsonInstanceRepository> logger)
    {
        _paths = paths;
        _store = store;
        _logger = logger;
    }

    public Task<IReadOnlyList<Instance>> ListAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<Instance>>(() => ReadAll(ct), ct);

    public Task<Instance?> GetAsync(string id, CancellationToken ct = default) =>
        Task.Run(() => IsSafeId(id) ? ReadOne(id) : null, ct);

    public async Task<Instance> CreateAsync(Instance draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await _mutation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                Directory.CreateDirectory(_paths.InstancesDirectory);

                var baseId = InstanceRules.ToFolderName(draft.Name);
                var id = baseId;
                for (var suffix = 2; Directory.Exists(InstanceDirectory(id)); suffix++) id = $"{baseId}-{suffix}";

                var directory = InstanceDirectory(id);
                Directory.CreateDirectory(Path.Combine(directory, GameDirectoryName));

                draft.Id = id;
                if (!_store.Write(DescriptionPath(id), ToFile(draft), JsonOptions))
                {
                    TryDeleteDirectory(directory);
                    throw new IOException($"Не удалось сохранить описание экземпляра «{draft.Name}».");
                }

                _logger.LogInformation("Создан экземпляр {Id} (Minecraft {Version}, {Loader}).", id, draft.McVersion, draft.Loader);
                return draft;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutation.Release();
        }
    }

    public async Task<bool> UpdateAsync(Instance instance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!IsSafeId(instance.Id)) return false;

        await _mutation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var read = _store.Read<InstanceFile>(DescriptionPath(instance.Id), JsonOptions);
                if (read.Value is null || read.Status is StoreReadStatus.Missing or StoreReadStatus.Corrupt or StoreReadStatus.Unavailable)
                {
                    _logger.LogWarning("Экземпляр {Id} не обновлён: описание отсутствует или недоступно ({Status}).", instance.Id, read.Status);
                    return false;
                }
                if (read.Value.SchemaVersion > InstanceFile.CurrentSchemaVersion)
                {
                    _logger.LogWarning("Экземпляр {Id} создан более новой версией лаунчера (формат {Version}) и не перезаписывается.", instance.Id, read.Value.SchemaVersion);
                    return false;
                }

                return _store.Write(DescriptionPath(instance.Id), ToFile(instance), JsonOptions);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutation.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!IsSafeId(id)) return false;

        await _mutation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var directory = InstanceDirectory(id);
                // Удаляем только папку, которая действительно является экземпляром: случайную папку с таким именем не трогаем.
                if (!File.Exists(DescriptionPath(id)) && !File.Exists(JsonFileStore.BackupPathFor(DescriptionPath(id)))) return false;

                if (!TryDeleteDirectory(directory)) return false;
                _logger.LogInformation("Экземпляр {Id} удалён.", id);
                return true;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutation.Release();
        }
    }

    public string GetGameDirectory(string id)
    {
        if (!IsSafeId(id)) throw new ArgumentException("Недопустимый идентификатор экземпляра.", nameof(id));
        return Path.Combine(InstanceDirectory(id), GameDirectoryName);
    }

    private List<Instance> ReadAll(CancellationToken ct)
    {
        var result = new List<Instance>();
        if (!Directory.Exists(_paths.InstancesDirectory)) return result;

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(_paths.InstancesDirectory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Не удалось прочитать папку экземпляров {Path}.", _paths.InstancesDirectory);
            return result;
        }

        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            var instance = ReadOne(Path.GetFileName(directory));
            if (instance is not null) result.Add(instance);
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    private Instance? ReadOne(string id)
    {
        var path = DescriptionPath(id);
        var read = _store.Read<InstanceFile>(path, JsonOptions);

        if (read.Value is null)
        {
            if (read.Status is StoreReadStatus.Corrupt or StoreReadStatus.Unavailable)
                _logger.LogWarning("Экземпляр {Id} пропущен: описание не читается ({Status}).", id, read.Status);
            return null; // Missing — обычная папка без описания, не экземпляр
        }

        var file = read.Value;
        if (file.SchemaVersion > InstanceFile.CurrentSchemaVersion)
        {
            _logger.LogWarning("Экземпляр {Id} создан более новой версией лаунчера (формат {Version}) и пропущен.", id, file.SchemaVersion);
            return null;
        }
        if (string.IsNullOrWhiteSpace(file.Name) || string.IsNullOrWhiteSpace(file.McVersion))
        {
            _logger.LogWarning("Экземпляр {Id} пропущен: в описании нет названия или версии игры.", id);
            return null;
        }

        return new Instance
        {
            Id = id,
            Name = file.Name,
            McVersion = file.McVersion,
            Loader = Enum.TryParse<LoaderKind>(file.Loader, ignoreCase: true, out var loader) ? loader : LoaderKind.Other,
            LoaderVersion = file.LoaderVersion,
            MinMemoryMb = file.MinMemoryMb,
            MaxMemoryMb = file.MaxMemoryMb,
            JvmArguments = file.JvmArguments ?? "",
            JavaPath = file.JavaPath,
            CreatedUtc = file.CreatedUtc,
            LastPlayedUtc = file.LastPlayedUtc,
            PlayTimeSeconds = Math.Max(0, file.PlayTimeSeconds),
        };
    }

    private static InstanceFile ToFile(Instance instance) => new()
    {
        SchemaVersion = InstanceFile.CurrentSchemaVersion,
        Name = instance.Name,
        McVersion = instance.McVersion,
        Loader = instance.Loader.ToString(),
        LoaderVersion = instance.LoaderVersion,
        MinMemoryMb = instance.MinMemoryMb,
        MaxMemoryMb = instance.MaxMemoryMb,
        JvmArguments = instance.JvmArguments,
        JavaPath = instance.JavaPath,
        CreatedUtc = instance.CreatedUtc,
        LastPlayedUtc = instance.LastPlayedUtc,
        PlayTimeSeconds = instance.PlayTimeSeconds,
    };

    private string InstanceDirectory(string id) => Path.Combine(_paths.InstancesDirectory, id);

    private string DescriptionPath(string id) => Path.Combine(InstanceDirectory(id), DescriptionFileName);

    /// <summary>Идентификатор — простое имя папки: без разделителей и «..», иначе удаление или чтение вышли бы за папку экземпляров.</summary>
    private static bool IsSafeId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id is not ("." or "..")
        && id == Path.GetFileName(id)
        && id.IndexOfAny(new[] { '/', '\\' }) < 0
        && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private bool TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Не удалось удалить папку {Path}.", directory);
            return false;
        }
    }
}
