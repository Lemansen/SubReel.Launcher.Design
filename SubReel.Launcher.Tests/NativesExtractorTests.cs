using SubReel.Launcher.Domain.Games;
using SubReel.Launcher.Minecraft.Installation;
using Microsoft.Extensions.Logging.Abstractions;

namespace SubReel.Launcher.Tests;

public sealed class NativesExtractorTests
{
    [Fact]
    public async Task Extracts_natives_for_this_platform_skipping_meta_inf_and_excluded()
    {
        using var world = new FakeGameWorld();
        var install = await world.Installer.InstallAsync(FakeGameWorld.VersionId);
        var target = Path.Combine(world.Dir.Root, "instance", "natives");

        var count = await world.Natives.ExtractAsync(install.Version!, target);

        Assert.Equal(2, count);
        Assert.Equal("win-native", await File.ReadAllTextAsync(Path.Combine(target, "old.dll")));
        Assert.Equal("k", await File.ReadAllTextAsync(Path.Combine(target, "sub", "dir", "keep.txt")));
        Assert.False(File.Exists(Path.Combine(target, "META-INF", "MANIFEST.MF")));
        Assert.False(Directory.Exists(Path.Combine(target, "excluded")));
        Assert.False(File.Exists(Path.Combine(target, "libold.so")));
    }

    [Fact]
    public async Task Extraction_is_repeatable_and_overwrites()
    {
        using var world = new FakeGameWorld();
        var install = await world.Installer.InstallAsync(FakeGameWorld.VersionId);
        var target = Path.Combine(world.Dir.Root, "natives");

        await world.Natives.ExtractAsync(install.Version!, target);
        await File.WriteAllTextAsync(Path.Combine(target, "old.dll"), "changed");
        await world.Natives.ExtractAsync(install.Version!, target);

        Assert.Equal("win-native", await File.ReadAllTextAsync(Path.Combine(target, "old.dll")));
    }

    [Fact]
    public async Task Linux_environment_extracts_linux_natives()
    {
        using var world = new FakeGameWorld();
        // Установка для Linux скачивает именно линуксовые файлы.
        var linux = new OsEnvironment("linux", "x86_64");
        var linuxInstaller = new GameInstaller(world.Metadata, world.Downloads, world.Planner, world.Layout, linux, NullLogger<GameInstaller>.Instance);
        var install = await linuxInstaller.InstallAsync(FakeGameWorld.VersionId);
        Assert.True(install.Succeeded, install.Error);
        var extractor = new NativesExtractor(world.Layout, linux, NullLogger<NativesExtractor>.Instance);
        var target = Path.Combine(world.Dir.Root, "natives-linux");

        var count = await extractor.ExtractAsync(install.Version!, target);

        Assert.Equal(1, count);
        Assert.True(File.Exists(Path.Combine(target, "libold.so")));
    }

    [Fact]
    public async Task Missing_native_jar_throws_file_not_found()
    {
        using var world = new FakeGameWorld();
        var version = await world.Metadata.ResolveAsync(FakeGameWorld.VersionId); // описание есть, файлы не скачаны

        await Assert.ThrowsAsync<FileNotFoundException>(() => world.Natives.ExtractAsync(version, Path.Combine(world.Dir.Root, "n")));
    }

    [Fact]
    public async Task Archive_with_path_traversal_is_rejected()
    {
        using var world = new FakeGameWorld();
        var evil = FakeGameWorld.Jar(("../evil.dll", "boom"));
        var library = new Library("evil:lib:1.0", null,
            new Dictionary<string, string> { ["windows"] = "natives-windows" },
            new Dictionary<string, LibraryArtifact> { ["natives-windows"] = new("evil/lib/1.0/lib-1.0-natives-windows.jar", new FileDownloadInfo("https://x", null, null)) },
            Array.Empty<Rule>(), Array.Empty<string>());
        var jar = world.Layout.LibraryPath("evil/lib/1.0/lib-1.0-natives-windows.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(jar)!);
        await File.WriteAllBytesAsync(jar, evil);
        var version = VersionResolver.Resolve("v", new[] { new GameVersionMetadata { Id = "v", MainClass = "M", Libraries = new[] { library } } });
        var target = Path.Combine(world.Dir.Root, "instance", "natives");

        await Assert.ThrowsAsync<InvalidDataException>(() => world.Natives.ExtractAsync(version, target));

        Assert.False(File.Exists(Path.Combine(world.Dir.Root, "instance", "evil.dll")));
    }
}
