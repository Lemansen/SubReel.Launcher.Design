using System.Runtime.InteropServices;
using SubReel.Launcher.Domain.Games;

namespace SubReel.Launcher.Minecraft.Installation;

/// <summary>Определяет платформу в терминах version.json.</summary>
public static class OsEnvironmentDetector
{
    public static OsEnvironment Detect()
    {
        var osName = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x86_64",
        };
        return new OsEnvironment(osName, arch, Environment.OSVersion.Version.ToString());
    }
}
