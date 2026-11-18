using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using HardwareTemplateBuilder.Core.Platform;
using HardwareTemplateBuilder.Wpf.Helpers;

namespace HardwareTemplateBuilder.Wpf;

/// <summary>
/// Entry point: single-instance guard, Start Menu shortcut, then the WPF <see cref="App"/>.
/// </summary>
internal static class Program
{
    // Named Mutex used to detect a second instance of this app running on the same machine.
    // Multiple instances on the same machine serve no purpose (they'd just be duplicate UIs
    // against the same centralized MySQL database) and can cause confusing double-prompts or
    // duplicate Start Menu shortcut writes. This is intentionally a local (non-"Global\") mutex:
    // other machines connect to the same shared MySQL server independently and are unaffected.
    private const string SingleInstanceMutexName = "HardwareTemplateBuilderWpf_SingleInstance";
    private static Mutex? _singleInstanceMutex;

    /// <summary>Starts the application.</summary>
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
                new InvalidOperationException("Startup aborted: another instance of HardwareTemplateBuilder 95 is already running on this machine."),
                isTerminating: false);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            CreateStartMenuShortcut();
        }

        try
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
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


    /// <summary>
    /// (Re)creates a plain Start Menu shortcut to this exe so the app shows up in Start search,
    /// after removing old AppUserModelIDs from shortcuts that point at this exe
    /// (<see cref="AppShortcuts.StripOldAppIds"/>).
    /// <para>
    /// The app deliberately runs without an AppUserModelID: Windows then identifies it by its exe
    /// path, the same as any ordinary program, which is what makes the taskbar right-click menu
    /// show the app name and "Pin to taskbar". Shortcuts made while older versions ran with an
    /// explicit ID (taskbar pins, desktop or Start Menu shortcuts) still carry that ID; when one
    /// points at this exe, Windows matches the window to it and the menu shows only
    /// "Close window". Recreating the Start Menu shortcut on every launch also keeps its target
    /// current after republishing. Best-effort: never throws.
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

            try
            {
                AppShortcuts.StripOldAppIds(exePath);
            }
            catch
            {
                // Best-effort.
            }

            var shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Hardware Template Builder 95.lnk");
            if (File.Exists(shortcutPath))
                File.Delete(shortcutPath);
            AppShortcuts.CreateShortcut(shortcutPath, exePath, "Hardware Template Builder 95");
        }
        catch
        {
            // Shortcut creation is best-effort; a failure must never crash the app.
        }
    }
}
