using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace SubReel.Launcher.Design.Rendering;

/// <summary>Превращает ProfileCosmetics в кисти, эффекты и готовые элементы интерфейса.</summary>
public static class CosmeticsRenderer
{
    private static readonly Color[] RainbowColors = BuildRainbow();

    private static readonly (string A, string B)[] AvatarPalette =
    {
        ("#3B6FE0", "#5AA9F0"), ("#7C3AED", "#A78BFA"), ("#059669", "#34D399"),
        ("#D97706", "#F59E0B"), ("#DC2626", "#F87171"), ("#0891B2", "#22D3EE"),
    };

    // ───────────────────────── Цвета ─────────────────────────

    private static Color[] BuildRainbow()
    {
        // 7 точек, последняя (360°) совпадает с первой (0°): анимация идёт по кругу без шва.
        var colors = new Color[7];
        for (var i = 0; i < colors.Length; i++) colors[i] = FromHsv(i * 60.0, 0.62, 1.0);
        return colors;
    }

    public static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static Color Parse(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex)) return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            // неверный цвет в настройках — берём запасной
        }
        return fallback;
    }

    /// <summary>
    /// Кисть для ника или рамки. null — «по умолчанию». Для Animated кисть бесконечно «переливается»:
    /// градиент повторяется (Repeat), а его сдвиг на ровно один период выглядит бесшовно.
    /// </summary>
    public static Brush? BuildBrush(ColorMode mode, string colorA, string colorB, bool animated)
    {
        if (mode == ColorMode.None) return null;
        var a = Parse(colorA, Colors.White);
        if (mode == ColorMode.Solid) return new SolidColorBrush(a);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            SpreadMethod = GradientSpreadMethod.Repeat,
        };

        if (mode == ColorMode.Rainbow)
        {
            for (var i = 0; i < RainbowColors.Length; i++)
                brush.GradientStops.Add(new GradientStop(RainbowColors[i], i / (double)(RainbowColors.Length - 1)));
        }
        else
        {
            var b = Parse(colorB, Colors.White);
            brush.GradientStops.Add(new GradientStop(a, 0));
            if (animated)
            {
                brush.GradientStops.Add(new GradientStop(b, 0.5));
                brush.GradientStops.Add(new GradientStop(a, 1)); // замыкаем круг
            }
            else
            {
                brush.GradientStops.Add(new GradientStop(b, 1));
            }
        }

        if (animated)
        {
            var shift = new TranslateTransform();
            brush.RelativeTransform = shift;
            var flow = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(3.6)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(flow, 30); // плавно, но без лишней нагрузки
            shift.BeginAnimation(TranslateTransform.XProperty, flow);
        }
        return brush;
    }

    // ───────────────────────── Ник и рамка ─────────────────────────

    public static void ApplyNick(TextBlock text, ProfileCosmetics c, Brush fallback) =>
        text.Foreground = BuildBrush(c.NickMode, c.NickColorA, c.NickColorB, c.NickAnimated) ?? fallback;

    /// <summary>Рамка кнопки профиля: кисть, толщина и (необязательно) свечение. Применяется к BorderBrush / BorderThickness / Effect.</summary>
    public static (Brush Brush, double Thickness, Effect? Glow) BuildFrame(ProfileCosmetics c, Brush fallback)
    {
        var brush = BuildBrush(c.FrameMode, c.FrameColorA, c.FrameColorB, c.FrameAnimated);
        if (brush is null) return (fallback, 1, null);

        Effect? glow = null;
        if (c.FrameGlow)
        {
            var color = c.FrameMode == ColorMode.Rainbow ? FromHsv(210, 0.6, 1) : Parse(c.FrameColorA, Colors.CornflowerBlue);
            glow = new DropShadowEffect { Color = color, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.5 };
        }
        return (brush, 1.5, glow);
    }

    // ───────────────────────── Аватарка ─────────────────────────

    public static Brush AvatarBrush(string name)
    {
        var hash = 0;
        foreach (var ch in name) hash = unchecked(hash * 31 + ch);
        var (c1, c2) = AvatarPalette[(hash & 0x7FFFFFFF) % AvatarPalette.Length];
        return new LinearGradientBrush((Color)ColorConverter.ConvertFromString(c1), (Color)ColorConverter.ConvertFromString(c2), 45);
    }

    public static string Initials(string name) =>
        name.Length >= 2 ? name[..2].ToUpperInvariant() : name.ToUpperInvariant();

    /// <summary>
    /// Слот аватарки: скруглённый квадрат (как голова скина). Сначала показываются буквы на цветном фоне,
    /// затем, когда загрузится картинка, она плавно проявляется поверх. Если задан ring — вокруг аватарки рамка.
    /// Вызывать из UI-потока.
    /// </summary>
    public static FrameworkElement BuildAvatar(string name, ProfileCosmetics c, double size, Brush? ring = null)
    {
        var inner = ring is null ? size : size - 8; // рамка 2 + отступ 2 с каждой стороны
        var radius = Math.Round(inner * 0.3);

        var face = new Border
        {
            Width = inner,
            Height = inner,
            CornerRadius = new CornerRadius(radius),
            Background = AvatarBrush(name),
            Child = new TextBlock
            {
                Text = Initials(name),
                FontSize = Math.Max(10, inner * 0.36),
                FontWeight = FontWeights.ExtraBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var stack = new Grid { Width = inner, Height = inner };
        stack.Children.Add(face);

        if (c.Avatar != AvatarKind.Initials)
        {
            var photo = new Border { CornerRadius = new CornerRadius(radius), Opacity = 0 };
            // Голова скина — пиксель-арт: масштабируем без сглаживания, чтобы пиксели оставались чёткими.
            RenderOptions.SetBitmapScalingMode(photo, c.Avatar == AvatarKind.SkinHead ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            stack.Children.Add(photo);
            _ = LoadPhotoAsync(photo, face, name, c);
        }

        if (ring is null) return stack;
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(radius + 4),
            BorderBrush = ring,
            BorderThickness = new Thickness(2),
            Padding = new Thickness(2),
            Child = stack,
        };
    }

    private static async Task LoadPhotoAsync(Border photo, Border face, string name, ProfileCosmetics c)
    {
        try
        {
            ImageSource? image = c.Avatar == AvatarKind.SkinHead
                ? await SkinHeads.LoadAsync(name)
                : await ImageDecoder.LoadFileAsync(c.AvatarImagePath, 256);
            if (image is null) return; // остаются буквы

            face.Child = null;
            photo.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            photo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }
        catch (Exception ex)
        {
            // аватарка — украшение, она не должна ронять интерфейс; сбой остаётся в журнале
            RenderingServices.Logger?.LogWarning(ex, "Не удалось показать аватарку аккаунта {Account}.", name);
        }
    }
}

/// <summary>Голова скина по нику. Для несуществующих ников сервис отдаёт стандартного Стива.</summary>
public static class SkinHeads
{
    public static async Task<ImageSource?> LoadAsync(string name) =>
        RenderingServices.Images is { } images
            ? await images.LoadAsync($"https://minotar.net/helm/{Uri.EscapeDataString(name)}/64.png", 64, TimeSpan.FromHours(12))
            : null;
}
