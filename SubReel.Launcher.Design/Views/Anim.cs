using System.Windows.Media.Animation;

namespace SubReel.Launcher.Design.Views;

/// <summary>Общие функции плавности для анимаций интерфейса.</summary>
internal static class Anim
{
    // cubic-bezier(0.25, 1, 0.5, 1) из веба ≈ easeOutQuart
    public static IEasingFunction OutQuart => new QuarticEase { EasingMode = EasingMode.EaseOut };
    public static IEasingFunction OutCubic => new CubicEase { EasingMode = EasingMode.EaseOut };
}

internal static class VisualTree
{
    /// <summary>Все потомки заданного типа в визуальном дереве (в порядке обхода).</summary>
    public static IEnumerable<T> Descendants<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
