using System.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using SubReel.Launcher.Application.Games;
using SubReel.Launcher.Minecraft.Launching;

namespace SubReel.Launcher.Tests;

/// <summary>Настоящие процессы: используется dotnet (он есть везде, где собирается проект) и системная пауза для проверки остановки.</summary>
public sealed class GameProcessTests
{
    private static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path ? path : "dotnet";

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

    [Fact]
    public async Task Output_goes_to_log_file_and_recent_lines_and_clean_exit_is_Exited()
    {
        using var dir = new TempDir();
        var logPath = dir.File(Path.Combine("logs", "out.log"));

        var process = GameProcess.Start(new GameProcessSpec(Dotnet, new[] { "--version" }, dir.Root, logPath, "[SubReel] заголовок"), NullLogger.Instance);
        var status = await process.WaitForExitAsync(Timeout());

        Assert.Equal(GameStatus.Exited, status);
        Assert.Equal(GameStatus.Exited, process.Status);
        Assert.Equal(0, process.ExitCode);
        Assert.NotEmpty(process.RecentOutput);

        var text = await File.ReadAllTextAsync(logPath);
        Assert.StartsWith("[SubReel] заголовок", text);
        Assert.Contains(process.RecentOutput[0], text);
        Assert.Contains("код 0", text);
    }

    [Fact]
    public async Task Non_zero_exit_code_is_Crashed()
    {
        using var dir = new TempDir();

        var process = GameProcess.Start(new GameProcessSpec(Dotnet, new[] { "exec", dir.File("missing.dll") }, dir.Root, null, null), NullLogger.Instance);
        var status = await process.WaitForExitAsync(Timeout());

        Assert.Equal(GameStatus.Crashed, status);
        Assert.NotEqual(0, process.ExitCode);
    }

    [Fact]
    public void Missing_executable_throws_and_leaves_no_open_log()
    {
        using var dir = new TempDir();
        var logPath = dir.File("out.log");

        Assert.Throws<Win32Exception>(() =>
            GameProcess.Start(new GameProcessSpec(dir.File("no-such-java.exe"), Array.Empty<string>(), dir.Root, logPath, "заголовок"), NullLogger.Instance));

        File.Delete(logPath); // файл закрыт: удаление не падает
    }

    [Fact]
    public async Task Killed_game_is_Exited_not_Crashed_and_status_event_fires()
    {
        using var dir = new TempDir();
        var spec = OperatingSystem.IsWindows()
            ? new GameProcessSpec("ping", new[] { "-n", "60", "127.0.0.1" }, dir.Root, null, null)
            : new GameProcessSpec("sleep", new[] { "60" }, dir.Root, null, null);
        var process = GameProcess.Start(spec, NullLogger.Instance);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.StatusChanged += (_, _) => changed.TrySetResult();

        Assert.Equal(GameStatus.Running, process.Status);
        Assert.Null(process.ExitCode);

        process.Kill();
        var status = await process.WaitForExitAsync(Timeout());
        await changed.Task.WaitAsync(Timeout());

        Assert.Equal(GameStatus.Exited, status);
        Assert.NotNull(process.ExitCode);
    }
}
