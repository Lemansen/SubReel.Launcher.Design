namespace SubReel.Launcher.Domain.Accounts;

public sealed record NickPreset(string Title, ColorMode Mode, string A, string B, bool Animated);
public sealed record FramePreset(string Title, ColorMode Mode, string A, string B, bool Animated, bool Glow);

/// <summary>
/// Готовые стили оформления. Чтобы добавить стиль, достаточно новой строки в <see cref="Nicks"/> / <see cref="Frames"/>.
/// Позже сюда же можно подмешивать стили, выданные сервером (SubReel ID).
/// </summary>
public static class CosmeticsPresets
{
    public static readonly IReadOnlyList<NickPreset> Nicks = new NickPreset[]
    {
        new("Обычный", ColorMode.None, "#FFFFFF", "#FFFFFF", false),
        new("Золото", ColorMode.Solid, "#FFD166", "#FFD166", false),
        new("Аква", ColorMode.Gradient, "#4C85FF", "#78C8FF", true),
        new("Закат", ColorMode.Gradient, "#FF7A59", "#FFC857", true),
        new("Радуга", ColorMode.Rainbow, "#FFFFFF", "#FFFFFF", true),
    };

    public static readonly IReadOnlyList<FramePreset> Frames = new FramePreset[]
    {
        new("Нет", ColorMode.None, "#4C85FF", "#78C8FF", false, false),
        new("Акцент", ColorMode.Solid, "#4C85FF", "#4C85FF", false, false),
        new("Золото", ColorMode.Gradient, "#FFD166", "#F59E0B", false, true),
        new("Неон", ColorMode.Gradient, "#4C85FF", "#C084FC", true, true),
        new("Радуга", ColorMode.Rainbow, "#FFFFFF", "#FFFFFF", true, true),
    };
}
