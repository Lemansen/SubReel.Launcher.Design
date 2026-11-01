using SubReel.Launcher.Application.Authentication;
using SubReel.Launcher.Application.Downloads;
using SubReel.Launcher.Application.Games;
using SubReel.Launcher.Application.Java;
using SubReel.Launcher.Application.Loaders;
using SubReel.Launcher.Domain.Accounts;
using SubReel.Launcher.Domain.Java;
using SubReel.Launcher.Domain.Versions;
using SubReel.Launcher.Minecraft.Launching;

namespace SubReel.Launcher.Tests;

internal sealed class FakeJavaManager : IJavaManager
{
    public JavaEnsureResult Result { get; set; } = new(
        new JavaInstallation(@"C:\java21", @"C:\java21\bin\java.exe", 21, "21.0.4", true, JavaSource.Managed),
        JavaSelectionStatus.Selected, VersionMismatch: false, Downloaded: false, Error: null);

    public JavaRequirement? LastRequirement { get; private set; }
    public string? LastExplicitPath { get; private set; }

    public Task<IReadOnlyList<JavaInstallation>> GetInstallationsAsync(bool refresh = false, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<JavaInstallation?> ProbeAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<JavaSelection> SelectAsync(JavaRequirement requirement, string? explicitJavaPath = null, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<JavaEnsureResult> EnsureAsync(JavaRequirement requirement, string? explicitJavaPath = null, IProgress<DownloadBatchProgress>? progress = null, CancellationToken ct = default)
    {
        LastRequirement = requirement;
        LastExplicitPath = explicitJavaPath;
        progress?.Report(new DownloadBatchProgress(1, 2, 10, 20));
        return Task.FromResult(Result);
    }
}

internal sealed class FakeSessionSource : IGameSessionSource
{
    public AuthenticationException? Failure { get; set; }
    public int Calls { get; private set; }

    public Task<ISession> GetSessionAsync(LauncherAccount account, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls++;
        if (Failure is not null) throw Failure;
        return Task.FromResult(OfflineIdentity.CreateSession(account.Name));
    }
}

internal sealed class FakeGameProcess : IGameProcess
{
    public FakeGameProcess(string gameDirectory) => GameDirectory = gameDirectory;

    public string GameDirectory { get; }
    public GameStatus Status { get; private set; } = GameStatus.Running;
    public int? ExitCode { get; private set; }
    public string? LogFilePath => null;
    public IReadOnlyList<string> RecentOutput => Array.Empty<string>();
    public bool Killed { get; private set; }

    public event EventHandler? StatusChanged;

    public Task<GameStatus> WaitForExitAsync(CancellationToken ct = default) => Task.FromResult(Status);

    public void Kill()
    {
        Killed = true;
        Exit(0);
    }

    /// <summary>Игра завершилась: 0 — нормально, иначе сбой.</summary>
    public void Exit(int code)
    {
        ExitCode = code;
        Status = code == 0 ? GameStatus.Exited : GameStatus.Crashed;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class RecordingStarter : IGameProcessStarter
{
    public List<GameProcessSpec> Specs { get; } = new();
    public Exception? Failure { get; set; }
    public FakeGameProcess? Last { get; private set; }

    public IGameProcess Start(GameProcessSpec spec)
    {
        Specs.Add(spec);
        if (Failure is not null) throw Failure;
        Last = new FakeGameProcess(spec.WorkingDirectory);
        return Last;
    }
}

/// <summary>Установщик загрузчиков без сети: записывает вызовы и отвечает именем профиля, как это делает настоящий.</summary>
internal sealed class FakeLoaderInstaller : ILoaderInstaller
{
    public List<(LoaderKind Kind, string McVersion, string? LoaderVersion)> Calls { get; } = new();
    public string LatestVersion { get; set; } = "0.16.5";
    public string? Failure { get; set; }

    public Task<LoaderInstallResult> EnsureAsync(LoaderKind kind, string mcVersion, string? loaderVersion, CancellationToken ct = default)
    {
        Calls.Add((kind, mcVersion, loaderVersion));
        if (Failure is not null) return Task.FromResult(LoaderInstallResult.Failure(Failure));

        var version = loaderVersion ?? LatestVersion;
        return Task.FromResult(LoaderInstallResult.Success(LoaderInfo.ProfileId(kind, mcVersion, version), version));
    }
}
