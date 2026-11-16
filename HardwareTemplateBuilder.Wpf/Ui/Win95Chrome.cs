using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace HardwareTemplateBuilder.Wpf.Ui;

/// <summary>
/// Code behind the custom Windows 95 title bar drawn by the <c>Win95Window</c> style
/// (Themes/Win95Extras.xaml). That style replaces the Windows 10/11 title bar via
/// <see cref="System.Windows.Shell.WindowChrome"/>; this class supplies what XAML can't:
/// the minimize/maximize/restore/close and system-menu commands for every <see cref="Window"/>,
/// square corners with no accent border on Windows 11, and the margin that keeps a maximized
/// window's frame from spilling off the screen edges.
/// Call <see cref="Register"/> once at startup.
/// </summary>
public static class Win95Chrome
{
    /// <summary>Opens the window's system menu under the caption icon (Win95 icon click).</summary>
    public static readonly RoutedCommand ShowSystemMenuCommand = new(nameof(ShowSystemMenuCommand), typeof(Win95Chrome));

    /// <summary>Name of the outermost element in the window template (gets the maximized margin).</summary>
    private const string RootPartName = "PART_Win95Root";

    private static bool _registered;

    /// <summary>Wires the caption commands and window fix-ups for every window in the app.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(
            SystemCommands.MinimizeWindowCommand,
            (s, _) => SystemCommands.MinimizeWindow((Window)s),
            (s, e) => e.CanExecute = ((Window)s).ResizeMode != ResizeMode.NoResize));
        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(
            SystemCommands.MaximizeWindowCommand,
            (s, _) => SystemCommands.MaximizeWindow((Window)s),
            (s, e) => e.CanExecute = CanResize((Window)s)));
        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(
            SystemCommands.RestoreWindowCommand,
            (s, _) => SystemCommands.RestoreWindow((Window)s),
            (s, e) => e.CanExecute = CanResize((Window)s)));
        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(
            SystemCommands.CloseWindowCommand,
            (s, _) => SystemCommands.CloseWindow((Window)s)));
        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(
            ShowSystemMenuCommand,
            (s, e) => ShowSystemMenu((Window)s, e.OriginalSource as FrameworkElement)));

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => OnWindowLoaded((Window)s)));
    }

    private static bool CanResize(Window w) =>
        w.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;

    private static void ShowSystemMenu(Window window, FrameworkElement? anchor)
    {
        var point = anchor != null
            ? anchor.PointToScreen(new Point(0, anchor.ActualHeight))
            : window.PointToScreen(new Point(0, 0));
        // PointToScreen returns device pixels; ShowSystemMenu wants DIPs.
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget != null)
            point = source.CompositionTarget.TransformFromDevice.Transform(point);
        SystemCommands.ShowSystemMenu(window, point);
    }

    private static void OnWindowLoaded(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                // Windows 11 only; harmless (ignored error code) on Windows 10.
                int doNotRound = DwmwcpDoNotRound;
                DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref doNotRound, sizeof(int));
                int noBorder = DwmwaColorNone;
                DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref noBorder, sizeof(int));
            }
        }
        catch
        {
            // Cosmetic only: no DWM (e.g. remote/basic session) just keeps the default corners.
        }

        window.StateChanged += (_, _) => UpdateMaximizedMargin(window);
        window.DpiChanged += (_, _) => UpdateMaximizedMargin(window);
        UpdateMaximizedMargin(window);
    }

    /// <summary>
    /// A maximized WindowChrome window is sized past the monitor by the resize frame on every
    /// side; pad the template root by that much so the caption and content stay on screen.
    /// </summary>
    private static void UpdateMaximizedMargin(Window window)
    {
        if (window.Template?.FindName(RootPartName, window) is not FrameworkElement root) return;

        if (window.WindowState != WindowState.Maximized)
        {
            root.Margin = new Thickness(0);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        double frameX = (GetSystemMetrics(SmCxSizeFrame) + GetSystemMetrics(SmCxPaddedBorder)) / dpi.DpiScaleX;
        double frameY = (GetSystemMetrics(SmCySizeFrame) + GetSystemMetrics(SmCxPaddedBorder)) / dpi.DpiScaleY;
        root.Margin = new Thickness(frameX, frameY, frameX, frameY);
    }

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);
    private const int SmCxSizeFrame = 32;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
