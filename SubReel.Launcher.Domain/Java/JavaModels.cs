using SubReel.Launcher.Domain.Games;

namespace SubReel.Launcher.Domain.Java;

/// <summary>Откуда известна эта Java. Порядок значений не важен, приоритет задаёт <see cref="JavaSelector"/>.</summary>
public enum JavaSource
{
    /// <summary>Рантайм, скачанный самим лаунчером.</summary>
    Managed,
    /// <summary>Рантайм официального лаунчера Minecraft (.minecraft/runtime).</summary>
    OfficialLauncher,
    JavaHome,
    Registry,
    KnownFolder,
    /// <summary>Выбрана пользователем вручную.</summary>
    Manual,
}

/// <summary>Найденная и прочитанная установка Java.</summary>
/// <param name="Home">Корень установки (в нём лежат bin и release).</param>
/// <param name="Executable">Полный путь к java / java.exe.</param>
/// <param name="MajorVersion">8, 17, 21 … (для «1.8.0_392» — 8).</param>
/// <param name="FullVersion">Версия как её сообщает Java («21.0.4»).</param>
/// <param name="Component">Имя рантайма Mojang для скачанных лаунчером установок.</param>
public sealed record JavaInstallation(
    string Home,
    string Executable,
    int MajorVersion,
    string FullVersion,
    bool Is64Bit,
    JavaSource Source,
    string? Vendor = null,
    string? Component = null);

/// <summary>Что требуется версии игры: мажорная версия Java и (если известно) рантайм Mojang, который можно скачать.</summary>
public sealed record JavaRequirement(int MajorVersion, string? Component)
{
    /// <summary>Версии без поля javaVersion (до 1.17) работают на Java 8; рантайм для них у Mojang называется jre-legacy.</summary>
    public static JavaRequirement FromVersion(ResolvedVersion version)
    {
        var major = version.JavaMajorVersion ?? 8;
        var component = !string.IsNullOrWhiteSpace(version.JavaComponent) ? version.JavaComponent : major == 8 ? "jre-legacy" : null;
        return new JavaRequirement(major, component);
    }
}

public enum JavaSelectionStatus
{
    /// <summary>Подходящая Java найдена среди установленных.</summary>
    Selected,
    /// <summary>Использован путь, который задал пользователь (мажорная версия может не совпадать, см. <see cref="JavaSelection.VersionMismatch"/>).</summary>
    ExplicitPathUsed,
    /// <summary>Подходящей Java нет, но её можно скачать.</summary>
    NeedsDownload,
    /// <summary>Подходящей Java нет и скачать нечего (неизвестен рантайм).</summary>
    NotFound,
    /// <summary>Заданный пользователем путь не ведёт к рабочей Java.</summary>
    ExplicitPathInvalid,
}

public sealed record JavaSelection(JavaInstallation? Installation, JavaSelectionStatus Status, bool VersionMismatch = false)
{
    public bool HasInstallation => Installation is not null;
}
