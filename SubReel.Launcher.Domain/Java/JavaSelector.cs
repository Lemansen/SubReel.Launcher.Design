namespace SubReel.Launcher.Domain.Java;

/// <summary>
/// Правило подбора Java по версии игры. Берётся только та же мажорная версия, что требует игра: старые версии Minecraft
/// ломаются на новой Java, а новые рассчитаны на конкретную. Среди подходящих выбирается по порядку: рантайм лаунчера,
/// рантайм официального лаунчера, остальные; затем 64-битная, затем более свежая сборка.
/// </summary>
public static class JavaSelector
{
    public static JavaSelection Select(JavaRequirement requirement, IReadOnlyList<JavaInstallation> installations, bool allow32Bit)
    {
        var best = installations
            .Where(i => i.MajorVersion == requirement.MajorVersion && (allow32Bit || i.Is64Bit))
            .OrderBy(i => Priority(i.Source))
            .ThenByDescending(i => i.Is64Bit)
            .ThenBy(i => i, FullVersionDescending.Instance)
            .FirstOrDefault();

        if (best is not null) return new JavaSelection(best, JavaSelectionStatus.Selected);
        return new JavaSelection(null, requirement.Component is null ? JavaSelectionStatus.NotFound : JavaSelectionStatus.NeedsDownload);
    }

    private static int Priority(JavaSource source) => source switch
    {
        JavaSource.Manual => 0,
        JavaSource.Managed => 1,
        JavaSource.OfficialLauncher => 2,
        _ => 3,
    };

    private sealed class FullVersionDescending : IComparer<JavaInstallation>
    {
        public static readonly FullVersionDescending Instance = new();
        public int Compare(JavaInstallation? x, JavaInstallation? y) => JavaVersionParser.Compare(y?.FullVersion, x?.FullVersion);
    }
}
