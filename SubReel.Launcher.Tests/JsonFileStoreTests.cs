using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Infrastructure.Storage;

namespace SubReel.Launcher.Tests;

public sealed class JsonFileStoreTests
{
    public sealed class Sample
    {
        public string Name { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static JsonFileStore NewStore() => new(NullLogger<JsonFileStore>.Instance);

    [Fact]
    public void Write_then_Read_roundtrips()
    {
        using var dir = new TempDir();
        var store = NewStore();
        var path = dir.File("a.json");

        Assert.True(store.Write(path, new Sample { Name = "один" }, Options));
        var result = store.Read<Sample>(path, Options);

        Assert.Equal(StoreReadStatus.Loaded, result.Status);
        Assert.Equal("один", result.Value!.Name);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Missing_file_is_reported_as_Missing()
    {
        using var dir = new TempDir();
        var result = NewStore().Read<Sample>(dir.File("none.json"), Options);

        Assert.Equal(StoreReadStatus.Missing, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Second_write_keeps_previous_version_as_backup()
    {
        using var dir = new TempDir();
        var store = NewStore();
        var path = dir.File("a.json");

        store.Write(path, new Sample { Name = "old" }, Options);
        store.Write(path, new Sample { Name = "new" }, Options);

        Assert.Contains("old", File.ReadAllText(JsonFileStore.BackupPathFor(path)));
        Assert.Contains("new", File.ReadAllText(path));
    }

    [Fact]
    public void Corrupt_file_is_restored_from_backup_and_kept_aside()
    {
        using var dir = new TempDir();
        var store = NewStore();
        var path = dir.File("a.json");
        store.Write(path, new Sample { Name = "old" }, Options);
        store.Write(path, new Sample { Name = "new" }, Options);
        File.WriteAllText(path, "{ не json");

        var result = store.Read<Sample>(path, Options);

        Assert.Equal(StoreReadStatus.RecoveredFromBackup, result.Status);
        Assert.Equal("old", result.Value!.Name);
        var quarantined = Directory.GetFiles(dir.Root, "a.json.corrupt-*");
        Assert.Single(quarantined);
        Assert.Equal("{ не json", File.ReadAllText(quarantined[0]));
    }

    [Fact]
    public void Corrupt_file_without_backup_is_preserved_not_deleted()
    {
        using var dir = new TempDir();
        var path = dir.File("a.json");
        File.WriteAllText(path, "garbage");

        var result = NewStore().Read<Sample>(path, Options);

        Assert.Equal(StoreReadStatus.Corrupt, result.Status);
        Assert.Null(result.Value);
        var quarantined = Assert.Single(Directory.GetFiles(dir.Root, "a.json.corrupt-*"));
        Assert.Equal("garbage", File.ReadAllText(quarantined));
    }

    [Fact]
    public void Utf8_bom_is_tolerated()
    {
        using var dir = new TempDir();
        var path = dir.File("a.json");
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat("""{"Name":"bom"}"""u8.ToArray()).ToArray());

        var result = NewStore().Read<Sample>(path, Options);

        Assert.Equal(StoreReadStatus.Loaded, result.Status);
        Assert.Equal("bom", result.Value!.Name);
    }
}
