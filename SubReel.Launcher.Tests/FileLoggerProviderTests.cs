using Microsoft.Extensions.Logging;
using SubReel.Launcher.Infrastructure.Logging;

namespace SubReel.Launcher.Tests;

public sealed class FileLoggerProviderTests
{
    [Fact]
    public void Writes_message_and_exception_to_daily_file()
    {
        using var dir = new TempDir();
        using (var provider = new FileLoggerProvider(dir.Root))
        {
            var logger = provider.CreateLogger("Test");
            logger.LogError(new InvalidOperationException("boom"), "Сообщение {Value}", 42);
        }

        var file = Assert.Single(Directory.GetFiles(dir.Root, "launcher-*.log"));
        var text = File.ReadAllText(file);
        Assert.Contains("[ERR] Test: Сообщение 42", text);
        Assert.Contains("boom", text);
    }
}
