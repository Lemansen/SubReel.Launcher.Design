using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SubReel.Launcher.Design.Views;

/// <summary>
/// Развёртывание окна без системной рамки. Само по себе такое окно при «Развернуть» занимает весь монитор и уходит под панель задач
/// (нижняя панель лаунчера оказывается закрыта). Здесь размер развёрнутого окна задаётся по рабочей области монитора, то есть без панели задач.
/// F11 включает настоящий полный экран: окно закрывает монитор целиком, включая панель задач.
/// </summary>
public partial class MainWindow
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 2;

    private bool _isFullScreen;
    private bool _changingState; // смену состояния начали мы сами: StateChanged не должен сбрасывать полноэкранный режим
    private WindowState _stateBeforeFullScreen = WindowState.Normal;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WindowProc);

        // Окно с прозрачностью система не считает «полноэкранным» и оставляет над ним панель задач. В полном экране окно поднимается над ней,
        // а когда пользователь переключается в другое приложение, опускается обратно (иначе оно закрывало бы всё остальное).
        Activated += (_, _) => { if (_isFullScreen) Topmost = true; };
        Deactivated += (_, _) => { if (_isFullScreen) Topmost = false; };
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        // Окно располагается в координатах монитора: рабочая область (без панели задач) или весь монитор в полноэкранном режиме.
        var area = _isFullScreen ? info.Monitor : info.Work;
        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMax.MaxPosition.X = area.Left - info.Monitor.Left;
        minMax.MaxPosition.Y = area.Top - info.Monitor.Top;
        minMax.MaxSize.X = area.Right - area.Left;
        minMax.MaxSize.Y = area.Bottom - area.Top;
        Marshal.StructureToPtr(minMax, lParam, fDeleteOld: true);

        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>F11: во весь экран и обратно. Окно возвращается в то состояние, из которого вошли.</summary>
    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            _stateBeforeFullScreen = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            _isFullScreen = true;
            _changingState = true;
            Topmost = true;
            WindowState = WindowState.Normal;     // чтобы система заново запросила размеры и применила полный монитор
            WindowState = WindowState.Maximized;
            _changingState = false;
            return;
        }

        ExitFullScreen(_stateBeforeFullScreen);
    }

    private void ExitFullScreen(WindowState target)
    {
        _isFullScreen = false;
        Topmost = false;
        _changingState = true;
        WindowState = WindowState.Normal;
        if (target == WindowState.Maximized) WindowState = WindowState.Maximized; // снова по рабочей области
        _changingState = false;
    }

    /// <summary>Окно перетащили или свернули — это уже не полный экран (иначе следующий F11 ничего бы не включал).</summary>
    private void LeaveFullScreenIfStateChanged()
    {
        if (!_isFullScreen || _changingState || WindowState == WindowState.Maximized) return;

        _isFullScreen = false;
        Topmost = false;
    }

    /// <summary>Кнопка «Развернуть» в шапке: из полноэкранного режима выходит, иначе переключает развёрнутое и обычное окно.</summary>
    private void ToggleMaximize()
    {
        if (_isFullScreen) ExitFullScreen(WindowState.Normal);
        else WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
