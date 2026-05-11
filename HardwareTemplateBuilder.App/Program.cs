using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HardwareTemplateBuilder.App;

class Program
{
    // P/Invoke for setting Application User Model ID (required for taskbar grouping on Windows)
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            SetCurrentProcessExplicitAppUserModelID("HardwareTemplateBuilder.App");
            CreateStartMenuShortcutIfNeeded();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Creates a Start Menu shortcut for this application on first run so that
    /// Windows 10/11 exposes the "Pin to taskbar" option when right-clicking it.
    /// Uses the WScript.Shell COM object — no extra packages required.
    /// Safe to call on every launch; exits immediately if the shortcut already exists.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void CreateStartMenuShortcutIfNeeded()
    {
        try
        {
            var programsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var shortcutPath = Path.Combine(programsFolder, "Hardware Template Builder.lnk");

            if (File.Exists(shortcutPath)) return;

            // Environment.ProcessPath returns the original launcher exe path, not the
            // temp extraction path used by single-file self-contained publish.
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            var wshType = Type.GetTypeFromProgID("WScript.Shell");
            if (wshType is null) return;

            dynamic wsh = Activator.CreateInstance(wshType)!;
            dynamic shortcut = wsh.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
            shortcut.Description = "Hardware Template Builder";
            shortcut.Save();
        }
        catch
        {
            // Shortcut creation is best-effort; a failure must never crash the app.
        }
    }
}
