using SubReel.Launcher.Infrastructure.Images;

namespace SubReel.Launcher.Tests;

public sealed class ImageCachePrunerTests
{
    private static string Make(TempDir dir, string name, int bytes, DateTime writtenUtc)
    {
        var path = dir.File(name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    [Fact]
    public void Oldest_images_are_removed_until_the_cache_fits_the_limit()
    {
        using var dir = new TempDir();
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var oldest = Make(dir, "a.img", 400, now.AddDays(-10));
        var middle = Make(dir, "b.img", 400, now.AddDays(-5));
        var newest = Make(dir, "c.img", 400, now.AddDays(-1));

        var freed = ImageCachePruner.Prune(dir.Root, limitBytes: 900, now);

        Assert.Equal(400, freed);
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void Cache_within_the_limit_is_left_alone()
    {
        using var dir = new TempDir();
        var now = DateTime.UtcNow;
        var image = Make(dir, "a.img", 100, now.AddDays(-30));

        Assert.Equal(0, ImageCachePruner.Prune(dir.Root, limitBytes: 1000, now));
        Assert.True(File.Exists(image));
    }

    [Fact]
    public void Stale_temp_files_are_removed_but_fresh_ones_stay()
    {
        using var dir = new TempDir();
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var stale = Make(dir, "a.img.tmp", 50, now.AddDays(-2));
        var fresh = Make(dir, "b.img.tmp", 50, now.AddMinutes(-5));

        ImageCachePruner.Prune(dir.Root, limitBytes: 1000, now);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Missing_folder_is_not_an_error() => Assert.Equal(0, ImageCachePruner.Prune(Path.Combine(Path.GetTempPath(), "subreel-no-such-folder-" + Guid.NewGuid().ToString("N"))));
}
