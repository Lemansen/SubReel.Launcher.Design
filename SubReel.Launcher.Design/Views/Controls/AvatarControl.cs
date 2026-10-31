using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SubReel.Launcher.Design.Views.Controls;

/// <summary>
/// Аватарка аккаунта: инициалы на цветном фоне, голова скина или своя картинка (по <see cref="Look"/>).
/// Перерисовывается при смене ника или оформления; несколько изменений подряд сливаются в одну перерисовку.
/// </summary>
public sealed class AvatarControl : ContentControl
{
    public static readonly DependencyProperty AccountNameProperty = DependencyProperty.Register(
        nameof(AccountName), typeof(string), typeof(AvatarControl), new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty LookProperty = DependencyProperty.Register(
        nameof(Look), typeof(ProfileLook), typeof(AvatarControl), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(AvatarControl), new PropertyMetadata(34.0, OnChanged));

    /// <summary>Размер, если вокруг аватарки есть кольцо (NaN — тот же, что Size).</summary>
    public static readonly DependencyProperty RingedSizeProperty = DependencyProperty.Register(
        nameof(RingedSize), typeof(double), typeof(AvatarControl), new PropertyMetadata(double.NaN, OnChanged));

    public static readonly DependencyProperty ShowRingProperty = DependencyProperty.Register(
        nameof(ShowRing), typeof(bool), typeof(AvatarControl), new PropertyMetadata(false, OnChanged));

    private bool _pending;

    public AvatarControl()
    {
        Focusable = false;
        IsTabStop = false;
    }

    public string AccountName { get => (string)GetValue(AccountNameProperty); set => SetValue(AccountNameProperty, value); }
    public ProfileLook? Look { get => (ProfileLook?)GetValue(LookProperty); set => SetValue(LookProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double RingedSize { get => (double)GetValue(RingedSizeProperty); set => SetValue(RingedSizeProperty, value); }
    public bool ShowRing { get => (bool)GetValue(ShowRingProperty); set => SetValue(ShowRingProperty, value); }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AvatarControl)d).Schedule();

    private void Schedule()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Rebuild));
    }

    private void Rebuild()
    {
        _pending = false;
        var cosmetics = Look?.Cosmetics ?? new ProfileCosmetics();
        var ring = ShowRing ? Look?.RingBrush : null;
        var size = ring is not null && !double.IsNaN(RingedSize) ? RingedSize : Size;
        Content = CosmeticsRenderer.BuildAvatar(AccountName ?? "", cosmetics, size, ring);
    }
}
