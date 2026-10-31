using System.Windows.Media;

namespace SubReel.Launcher.Design.Rendering;

/// <summary>Цвет загрузчика в списке версий и на подписях. Название и монограмма — в Domain (<see cref="LoaderInfo"/>), цвета — здесь, потому что это WPF.</summary>
public static class LoaderAccent
{
    public static Color ColorFor(LoaderKind kind) => kind switch
    {
        LoaderKind.Vanilla => Color.FromRgb(0x78, 0xA8, 0xFF),
        LoaderKind.Fabric => Color.FromRgb(0xD6, 0xC6, 0x9B),
        LoaderKind.Forge => Color.FromRgb(0xF0, 0x7B, 0x4A),
        LoaderKind.NeoForge => Color.FromRgb(0xF5, 0xA2, 0x3C),
        LoaderKind.Quilt => Color.FromRgb(0xB0, 0x82, 0xFF),
        _ => Color.FromRgb(0xA1, 0xA1, 0xAA),
    };
}
