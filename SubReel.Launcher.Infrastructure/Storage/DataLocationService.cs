using System.Text.Json;
using SubReel.Launcher.Application.Abstractions;

namespace SubReel.Launcher.Infrastructure.Storage;

/// <summary>
/// Выбор папки данных. Указатель на выбранную папку лежит в маленьком файле <c>location.json</c> в папке по умолчанию (в выбранной папке его хранить нельзя:
/// чтобы её найти, указатель нужен раньше). Всё остальное лаунчер ищет уже в выбранной папке.
/// <para>
/// <see cref="Start"/> вызывается один раз при запуске, до открытия любых файлов данных и до создания журнала. Он:
/// (1) определяет корень (недоступный выбранный корень — на этот сеанс заменяется папкой по умолчанию, указатель не трогается);
/// (2) если пользователь сменил папку, переносит в неё зоны launcher / shared / profiles из прежней;
/// (3) переносит данные из прежней раскладки (%AppData%\SubReel и корень без зон) в зоны. Ничего не перезаписывается и не удаляется до успешного копирования.
/// Что именно было сделано, сообщает <see cref="StartupNotes"/> — журнал в это время ещё не создан.
/// </para>
/// </summary>
public sealed class DataLocationService : IDataLocationService
{
    private const string StateFileName = "location.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly string _stateFile;
    private readonly List<string> _notes = new();
    private LocationState _state = new();

    private DataLocationService(string defaultRoot)
    {
        DefaultRoot = Path.GetFullPath(defaultRoot);
        CurrentRoot = DefaultRoot;
        _stateFile = Path.Combine(DefaultRoot, StateFileName);
    }

    public string DefaultRoot { get; }
    public string CurrentRoot { get; private set; }
    public bool IsCustom => !SamePath(CurrentRoot, DefaultRoot);

    /// <summary>Выбранная папка недоступна, на этот сеанс использована папка по умолчанию.</summary>
    public bool FellBackToDefault { get; private set; }

    /// <summary>Что сделал запуск: перенос данных, предупреждения. Пусто — ничего не происходило.</summary>
    public IReadOnlyList<string> StartupNotes => _notes;

    public string? PendingRoot
    {
        get
        {
            var target = Path.GetFullPath(_state.Root ?? DefaultRoot);
            return SamePath(target, CurrentRoot) ? null : target;
        }
    }

    /// <summary>Запуск с настоящими папками пользователя.</summary>
    public static DataLocationService StartDefault() => Start(AppPaths.DefaultRoot, AppPaths.LegacyRoamingRoot, AppPaths.LegacyLocalRoot);

    /// <param name="legacyRoamingRoot">Прежнее место настроек, аватарок и сборок; null — не переносить.</param>
    /// <param name="legacyLocalRoot">Прежнее место кэша, журналов, токенов и общих файлов игры; null — не переносить.</param>
    public static DataLocationService Start(string defaultRoot, string? legacyRoamingRoot, string? legacyLocalRoot)
    {
        var service = new DataLocationService(defaultRoot);
        service.Initialize(legacyRoamingRoot, legacyLocalRoot);
        return service;
    }

    public DataLocationChange Change(string chosenFolder) => ChangeCore(chosenFolder, createSubfolderIfForeign: true);

    public DataLocationChange ResetToDefault() => ChangeCore(DefaultRoot, createSubfolderIfForeign: false);

    // ───────────────────────── Выбор новой папки ─────────────────────────

    private DataLocationChange ChangeCore(string chosenFolder, bool createSubfolderIfForeign)
    {
        if (string.IsNullOrWhiteSpace(chosenFolder) || !Path.IsPathFullyQualified(chosenFolder))
            return new DataLocationChange(false, null, DataLocationError.NotAbsolute);

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosenFolder));

        // Чужая непустая папка: данные лаунчера кладём в подпапку, чтобы не смешивать их с чужими файлами.
        if (createSubfolderIfForeign && !SamePath(full, CurrentRoot) && Directory.Exists(full) && HasEntries(full) && !LooksLikeDataRoot(full))
            full = Path.Combine(full, "SubReel");

        if (SamePath(full, CurrentRoot))
        {
            // Вернулись на текущую папку: ожидающая смена отменяется.
            return Save(new LocationState { Root = SamePath(full, DefaultRoot) ? null : full }, full);
        }

        if (IsInside(full, CurrentRoot)) return new DataLocationChange(false, null, DataLocationError.NestedInCurrent);
        if (IsInside(CurrentRoot, full)) return new DataLocationChange(false, null, DataLocationError.ContainsCurrent);
        if (!CanWrite(full)) return new DataLocationChange(false, null, DataLocationError.NotWritable);

        return Save(new LocationState { Root = SamePath(full, DefaultRoot) ? null : full, MoveFrom = CurrentRoot }, full);
    }

    private DataLocationChange Save(LocationState state, string root)
    {
        if (!TryWriteState(state)) return new DataLocationChange(false, null, DataLocationError.NotWritable);
        _state = state;
        return new DataLocationChange(true, root, DataLocationError.None);
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".subreel-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    // ───────────────────────── Запуск ─────────────────────────

    private void Initialize(string? legacyRoamingRoot, string? legacyLocalRoot)
    {
        TryCreate(DefaultRoot);
        _state = ReadState();

        var root = Path.GetFullPath(_state.Root ?? DefaultRoot);
        if (!SamePath(root, DefaultRoot) && !TryCreate(root))
        {
            _notes.Add($"Папка данных «{root}» недоступна (диск отключён или нет прав). В этом сеансе используется стандартная «{DefaultRoot}»; выбор папки сохранён.");
            root = DefaultRoot;
            FellBackToDefault = true;
        }
        CurrentRoot = root;

        if (!FellBackToDefault && _state.MoveFrom is { Length: > 0 } from && !SamePath(from, root))
            MovePendingData(from, root);

        if (legacyRoamingRoot is not null) MigrateLegacyRoaming(legacyRoamingRoot, root);
        if (legacyLocalRoot is not null) MigrateLegacyLocal(legacyLocalRoot, root);
    }

    /// <summary>Пользователь сменил папку данных: переносим зоны из прежней. Указатель на старое место снимается, если переносить больше нечего.</summary>
    private void MovePendingData(string from, string to)
    {
        var retry = false;
        foreach (var zone in AppPaths.Zones)
        {
            var source = Path.Combine(from, zone);
            var target = Path.Combine(to, zone);
            if (!Directory.Exists(source)) continue;

            // Пустая папка зоны, созданная заранее, не мешает; непустая — данных там уже что-то есть, их не трогаем.
            if (Directory.Exists(target) && !HasEntries(target)) TryDeleteEmpty(target);

            if (StorageMover.MoveDirectory(source, target, out var problem))
            {
                _notes.Add($"Перенесено: «{source}» → «{target}».");
                if (problem is not null) _notes.Add(problem);
            }
            else if (Directory.Exists(target))
            {
                _notes.Add($"Не перенесено: в «{target}» уже есть данные, «{source}» оставлена на месте.");
            }
            else
            {
                _notes.Add($"Не удалось перенести «{source}» → «{target}»: {problem}. Повторим при следующем запуске.");
                retry = true;
            }
        }

        if (!retry) TryWriteState(new LocationState { Root = _state.Root });
        if (!retry) _state = new LocationState { Root = _state.Root };
    }

    private void MigrateLegacyRoaming(string legacyRoot, string root)
    {
        if (!Directory.Exists(legacyRoot) || SamePath(legacyRoot, root)) return;

        var launcher = Path.Combine(root, AppPaths.LauncherZone);
        MigrateFiles(legacyRoot, "design-settings.json*", launcher);
        MigrateDirectory(Path.Combine(legacyRoot, "avatars"), Path.Combine(launcher, "avatars"));
        MigrateDirectory(Path.Combine(legacyRoot, "instances"), Path.Combine(root, AppPaths.ProfilesZone));
    }

    private void MigrateLegacyLocal(string legacyRoot, string root)
    {
        if (!Directory.Exists(legacyRoot)) return;

        var launcher = Path.Combine(root, AppPaths.LauncherZone);
        var shared = Path.Combine(root, AppPaths.SharedZone);
        MigrateDirectory(Path.Combine(legacyRoot, "cache"), Path.Combine(launcher, "cache"));
        MigrateDirectory(Path.Combine(legacyRoot, "logs"), Path.Combine(launcher, "logs"));
        MigrateDirectory(Path.Combine(legacyRoot, "auth"), Path.Combine(launcher, "auth"));
        MigrateDirectory(Path.Combine(legacyRoot, "game"), Path.Combine(shared, "game"));
        MigrateDirectory(Path.Combine(legacyRoot, "runtimes"), Path.Combine(shared, "runtimes"));
    }

    private void MigrateDirectory(string source, string target)
    {
        if (!Directory.Exists(source)) return;

        if (StorageMover.MoveDirectory(source, target, out var problem))
        {
            _notes.Add($"Данные перенесены в новую раскладку: «{source}» → «{target}».");
            if (problem is not null) _notes.Add(problem);
        }
        else
        {
            _notes.Add($"Прежняя папка «{source}» оставлена на месте: {problem}");
        }
    }

    private void MigrateFiles(string sourceDirectory, string pattern, string targetDirectory)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(sourceDirectory, pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notes.Add($"Не удалось прочитать «{sourceDirectory}»: {ex.Message}");
            return;
        }

        foreach (var file in files)
        {
            var target = Path.Combine(targetDirectory, Path.GetFileName(file));
            if (StorageMover.MoveFile(file, target, out var problem)) _notes.Add($"Файл перенесён: «{file}» → «{target}».");
            else _notes.Add($"Файл «{file}» оставлен на месте: {problem}");
        }
    }

    // ───────────────────────── Указатель ─────────────────────────

    private LocationState ReadState()
    {
        try
        {
            if (!File.Exists(_stateFile)) return new LocationState();
            return JsonSerializer.Deserialize<LocationState>(File.ReadAllText(_stateFile), JsonOptions) ?? new LocationState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _notes.Add($"Файл «{_stateFile}» не прочитан ({ex.Message}); используется папка данных по умолчанию.");
            return new LocationState();
        }
    }

    private bool TryWriteState(LocationState state)
    {
        try
        {
            Directory.CreateDirectory(DefaultRoot);
            var temp = _stateFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, _stateFile, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notes.Add($"Не удалось записать «{_stateFile}»: {ex.Message}");
            return false;
        }
    }

    // ───────────────────────── Мелочи ─────────────────────────

    private static bool TryCreate(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteEmpty(string directory)
    {
        try
        {
            Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не удалилась — перенос сообщит, что место занято
        }
    }

    private static bool HasEntries(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // не можем проверить — считаем занятой
        }
    }

    private static bool LooksLikeDataRoot(string directory) => AppPaths.Zones.Any(zone => Directory.Exists(Path.Combine(directory, zone)));

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>child лежит строго внутри parent.</summary>
    private static bool IsInside(string child, string parent)
    {
        var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return c.Length > p.Length
            && c.StartsWith(p, StringComparison.OrdinalIgnoreCase)
            && (c[p.Length] == Path.DirectorySeparatorChar || c[p.Length] == Path.AltDirectorySeparatorChar);
    }

    private sealed class LocationState
    {
        public int SchemaVersion { get; set; } = 1;

        /// <summary>Выбранная папка данных; null — по умолчанию.</summary>
        public string? Root { get; set; }

        /// <summary>Где лежат данные, которые нужно перенести в <see cref="Root"/> при следующем запуске.</summary>
        public string? MoveFrom { get; set; }
    }
}
