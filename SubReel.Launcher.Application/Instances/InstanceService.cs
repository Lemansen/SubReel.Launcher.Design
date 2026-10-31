using Microsoft.Extensions.Logging;
using SubReel.Launcher.Domain.Instances;
using SubReel.Launcher.Domain.Versions;

namespace SubReel.Launcher.Application.Instances;

/// <summary>Результат операции над экземпляром: либо экземпляр, либо причина отказа.</summary>
public sealed record InstanceOperationResult(Instance? Instance, InstanceValidationError Error, bool StorageFailed = false)
{
    public bool Succeeded => Instance is not null;
}

/// <summary>Сценарии работы с экземплярами: создание, переименование, настройки запуска, отметка запуска, удаление.</summary>
public sealed class InstanceService
{
    private readonly IInstanceRepository _repository;
    private readonly TimeProvider _time;
    private readonly ILogger<InstanceService> _logger;

    public InstanceService(IInstanceRepository repository, TimeProvider time, ILogger<InstanceService> logger)
    {
        _repository = repository;
        _time = time;
        _logger = logger;
    }

    public Task<IReadOnlyList<Instance>> ListAsync(CancellationToken ct = default) => _repository.ListAsync(ct);

    public Task<Instance?> GetAsync(string id, CancellationToken ct = default) => _repository.GetAsync(id, ct);

    public async Task<InstanceOperationResult> CreateAsync(string name, string mcVersion, LoaderKind loader = LoaderKind.Vanilla, string? loaderVersion = null, CancellationToken ct = default)
    {
        var draft = new Instance
        {
            Name = name.Trim(),
            McVersion = mcVersion.Trim(),
            Loader = loader,
            LoaderVersion = loader == LoaderKind.Vanilla ? null : loaderVersion,
            CreatedUtc = _time.GetUtcNow().UtcDateTime,
        };

        var existing = await _repository.ListAsync(ct).ConfigureAwait(false);
        var error = InstanceRules.Validate(draft, existing);
        if (error != InstanceValidationError.None) return new InstanceOperationResult(null, error);

        try
        {
            var created = await _repository.CreateAsync(draft, ct).ConfigureAwait(false);
            return new InstanceOperationResult(created, InstanceValidationError.None);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Не удалось создать экземпляр «{Name}».", draft.Name);
            return new InstanceOperationResult(null, InstanceValidationError.None, StorageFailed: true);
        }
    }

    public async Task<InstanceOperationResult> RenameAsync(string id, string newName, CancellationToken ct = default) =>
        await ChangeAsync(id, instance => instance.Name = newName.Trim(), ct).ConfigureAwait(false);

    /// <summary>Меняет память, аргументы JVM и путь к Java. Пустые значения означают «по умолчанию».</summary>
    public async Task<InstanceOperationResult> UpdateLaunchSettingsAsync(string id, int? minMemoryMb, int? maxMemoryMb, string? jvmArguments, string? javaPath, CancellationToken ct = default) =>
        await ChangeAsync(id, instance =>
        {
            instance.MinMemoryMb = minMemoryMb;
            instance.MaxMemoryMb = maxMemoryMb;
            instance.JvmArguments = jvmArguments?.Trim() ?? "";
            instance.JavaPath = string.IsNullOrWhiteSpace(javaPath) ? null : javaPath.Trim();
        }, ct).ConfigureAwait(false);

    public async Task<InstanceOperationResult> MarkPlayedAsync(string id, CancellationToken ct = default) =>
        await ChangeAsync(id, instance => instance.LastPlayedUtc = _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => _repository.DeleteAsync(id, ct);

    private async Task<InstanceOperationResult> ChangeAsync(string id, Action<Instance> change, CancellationToken ct)
    {
        var all = await _repository.ListAsync(ct).ConfigureAwait(false);
        var instance = all.FirstOrDefault(i => i.Id == id);
        if (instance is null) return new InstanceOperationResult(null, InstanceValidationError.None, StorageFailed: true);

        change(instance);
        var error = InstanceRules.Validate(instance, all);
        if (error != InstanceValidationError.None) return new InstanceOperationResult(null, error);

        var saved = await _repository.UpdateAsync(instance, ct).ConfigureAwait(false);
        return saved
            ? new InstanceOperationResult(instance, InstanceValidationError.None)
            : new InstanceOperationResult(null, InstanceValidationError.None, StorageFailed: true);
    }
}
