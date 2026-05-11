using Avalonia;
using System;
using System.Runtime.InteropServices;

namespace HardwareTemplateBuilder.App;

class Program
{
    // P/Invoke for setting Application User Model ID (required for taskbar pinning on Windows)
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Set Application User Model ID for Windows taskbar pinning
        if (OperatingSystem.IsWindows())
        {
            SetCurrentProcessExplicitAppUserModelID("HardwareTemplateBuilder.App");
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
