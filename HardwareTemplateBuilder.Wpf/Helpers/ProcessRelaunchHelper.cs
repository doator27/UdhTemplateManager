using System;
using System.Diagnostics;
using System.Windows;

namespace HardwareTemplateBuilder.Wpf.Helpers;

/// <summary>
/// Restarts the application by launching a new instance and closing the current one.
/// Used after operations where in-memory session state would otherwise be stale.
/// </summary>
public static class ProcessRelaunchHelper
{
    /// <summary>
    /// Launches a new instance of the running application, then closes <paramref name="currentWindow"/>
    /// (which shuts the app down, since <c>ShutdownMode</c> is <c>OnMainWindowClose</c>).
    /// </summary>
    /// <returns>False if the executable path is unknown or launching failed.</returns>
    public static bool RelaunchAndExit(Window currentWindow)
    {
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
