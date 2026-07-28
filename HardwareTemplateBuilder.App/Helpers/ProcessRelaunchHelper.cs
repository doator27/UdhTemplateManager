using System;
using System.Diagnostics;
using Avalonia.Controls;

namespace HardwareTemplateBuilder.App.Helpers;

/// <summary>
/// Restarts the application by launching a new instance and closing the current one.
/// Used after operations (e.g. restoring a database backup) where in-memory session state
/// would otherwise be stale against data that changed on disk underneath the running process.
/// </summary>
public static class ProcessRelaunchHelper
{
    /// <summary>
    /// Launches a new instance of the running application, then closes <paramref name="currentWindow"/>.
    /// Closing the main window triggers full app shutdown (Avalonia's default <c>ShutdownMode</c> is
    /// <c>OnMainWindowClose</c>), the same mechanism the File menu's Exit item already relies on.
    /// </summary>
    /// <param name="currentWindow">The application's main window.</param>
    /// <returns>
    /// True if a new instance was launched and the current window closed; false if the running
    /// executable's path could not be determined or launching it failed — callers should fall
    /// back to asking the user to restart manually in that case.
    /// </returns>
    public static bool RelaunchAndExit(Window currentWindow)
    {
        // Environment.ProcessPath resolves to the original launcher exe, not the temp
        // extraction path used by single-file self-contained publish (see Program.cs).
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
            currentWindow.Close();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
