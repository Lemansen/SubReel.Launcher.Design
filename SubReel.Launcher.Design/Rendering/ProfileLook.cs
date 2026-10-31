using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace SubReel.Launcher.Design.Rendering;

/// <summary>Кисти из системной темы, нужные ViewModel-ям (чтобы не искать ресурсы в коде окна).</summary>
public static class ThemeBrushes
{
    public static Brush TextMain => Lookup("TextMain", Brushes.White);
    public static Brush TextMuted => Lookup("TextMuted", Brushes.Gray);

    private static Brush Lookup(string key, Brush fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? fallback;
}

/// <summary>
/// Готовый «внешний вид» профиля: кисть ника, рамка кнопки, кольцо вокруг аватарки. Строится из <see cref="ProfileCosmetics"/>
/// (модель) и отдаётся привязкам; отрисовку выполняет <see cref="CosmeticsRenderer"/>. Кисти создаются лениво:
/// переливающаяся кисть — это запущенная анимация, поэтому строим только то, что реально показывается.
/// </summary>
public sealed class ProfileLook
{
    private Brush? _nick;
    private (Brush Brush, double Thickness, Effect? Glow)? _frame;
    private Brush? _ring;
    private bool _ringBuilt;

    public ProfileLook(ProfileCosmetics cosmetics) => Cosmetics = cosmetics;

    public ProfileCosmetics Cosmetics { get; }

    /// <summary>Кисть ника; для стиля «Обычный» — основной цвет текста темы.</summary>
    public Brush NickBrush => _nick ??= CosmeticsRenderer.BuildBrush(
        Cosmetics.NickMode, Cosmetics.NickColorA, Cosmetics.NickColorB, Cosmetics.NickAnimated) ?? ThemeBrushes.TextMain;

    private (Brush Brush, double Thickness, Effect? Glow) Frame =>
        _frame ??= CosmeticsRenderer.BuildFrame(Cosmetics, new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));

    public Brush FrameBrush => Frame.Brush;
    public Thickness FrameThickness => new(Frame.Thickness);
    public Effect? FrameGlow => Frame.Glow;

    /// <summary>Кольцо вокруг аватарки в меню профиля (того же стиля, что рамка кнопки); null — рамка выключена.</summary>
    public Brush? RingBrush
    {
        get
        {
            if (_ringBuilt) return _ring;
            _ringBuilt = true;
            return _ring = CosmeticsRenderer.BuildBrush(Cosmetics.FrameMode, Cosmetics.FrameColorA, Cosmetics.FrameColorB, Cosmetics.FrameAnimated);
        }
    }

    /// <summary>Только ник с кистью стиля (для подписей на кнопках-вариантах).</summary>
    public static Brush NickBrushFor(NickPreset preset) =>
        new ProfileLook(new ProfileCosmetics
        {
            NickMode = preset.Mode,
            NickColorA = preset.A,
            NickColorB = preset.B,
            NickAnimated = preset.Animated,
        }).NickBrush;
}
