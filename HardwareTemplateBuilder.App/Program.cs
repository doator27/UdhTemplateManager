using Avalonia;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using HardwareTemplateBuilder.App.Helpers;

namespace HardwareTemplateBuilder.App;

class Program
{
    // Named Mutex used to detect a second instance of this app running on the same machine.
    // Multiple instances on the same machine serve no purpose (they'd just be duplicate UIs
    // against the same centralized MySQL database) and can cause confusing double-prompts or
    // duplicate Start Menu shortcut writes. This is intentionally a local (non-"Global\") mutex:
    // other machines connect to the same shared MySQL server independently and are unaffected.
    private const string SingleInstanceMutexName = "HardwareTemplateBuilder_SingleInstance";
    private static Mutex? _singleInstanceMutex;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        CrashLogger.RegisterGlobalHandlers();

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // Another instance of this app is already running on this machine. Exit quietly
            // rather than opening a redundant duplicate window against the same shared database.
            CrashLogger.Log(
                new InvalidOperationException("Startup aborted: another instance of HardwareTemplateBuilder is already running on this machine."),
                isTerminating: false);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            CreateStartMenuShortcut();
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, isTerminating: true);
            throw;
        }
        finally
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// (Re)creates a plain Start Menu shortcut to this exe so the app shows up in Start search.
    /// <para>
    /// Deliberately has no AppUserModelID, and the process doesn't set one either: Windows then
    /// identifies the app by its exe path, the same as any ordinary program, which is what makes
    /// the taskbar right-click menu show the app name and "Pin to taskbar". (Earlier versions set
    /// an explicit ID on the process and this shortcut; when the two didn't line up, the menu
    /// showed only "Close window".) Recreating it on every launch also clears the ID from
    /// shortcuts made by those versions and keeps the target current after republishing.
    /// Best-effort: never throws.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void CreateStartMenuShortcut()
    {
        try
        {
            // Environment.ProcessPath is the launcher exe, not single-file publish's temp extraction path.
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            var shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Hardware Template Builder.lnk");
            if (File.Exists(shortcutPath))
                File.Delete(shortcutPath);

            var wshType = Type.GetTypeFromProgID("WScript.Shell");
            if (wshType is null) return;

            dynamic wsh = Activator.CreateInstance(wshType)!;
            dynamic shortcut = wsh.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
            shortcut.Description = "Hardware Template Builder";
            shortcut.IconLocation = exePath + ",0";
            shortcut.Save();
        }
        catch
        {
            // Shortcut creation is best-effort; a failure must never crash the app.
        }
    }
}
