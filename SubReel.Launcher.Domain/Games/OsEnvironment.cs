namespace SubReel.Launcher.Domain.Games;

/// <summary>Платформа, для которой выбираются библиотеки и аргументы. Имена как в version.json: windows / linux / osx.</summary>
/// <param name="OsName">windows, linux или osx.</param>
/// <param name="Arch">x86 (32 бита), x86_64 или arm64.</param>
/// <param name="OsVersion">Версия системы; нужна только правилам с полем version.</param>
/// <param name="Features">Включённые возможности (is_demo_user, has_custom_resolution …); отсутствующие считаются выключенными.</param>
public sealed record OsEnvironment(string OsName, string Arch, string? OsVersion = null, IReadOnlyDictionary<string, bool>? Features = null)
{
    public bool Is64Bit => Arch != "x86";
}
