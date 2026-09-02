using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace HardwareTemplateBuilder.App.Helpers;

/// <summary>
/// Writes unhandled exceptions to a "CrashReports.txt" file located next to the executable.
/// Newest entries are always inserted at the top of the file (reverse chronological order).
/// </summary>
public static class CrashLogger
{
    private static readonly object LockObject = new();

    /// <summary>
    /// Registers handlers for unhandled exceptions on the current AppDomain and
    /// for unobserved exceptions from Tasks so they are recorded before the app exits.
    /// </summary>
    public static void RegisterGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log(e.ExceptionObject as Exception, isTerminating: e.IsTerminating);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log(e.Exception, isTerminating: false);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Appends a crash entry to CrashReports.txt, inserting it above any existing content
    /// so the file always reads newest-first.
    /// </summary>
    public static void Log(Exception? exception, bool isTerminating = false)
    {
        try
        {
            var userName = SessionService.ActiveUserProfile?.UserName ?? Environment.UserName;

            var entry = new StringBuilder();
            entry.AppendLine(new string('=', 80));
            entry.AppendLine($"Date/Time : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            entry.AppendLine($"User      : {userName}");
            entry.AppendLine($"Fatal     : {isTerminating}");
            entry.AppendLine($"Error     : {exception?.GetType().FullName ?? "Unknown"} - {exception?.Message ?? "No exception details available."}");
            entry.AppendLine("Details   :");
            entry.AppendLine(exception?.ToString() ?? "No additional details available.");
            entry.AppendLine(new string('=', 80));
            entry.AppendLine();

            var logPath = GetLogFilePath();

            lock (LockObject)
            {
                var existing = File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;
                File.WriteAllText(logPath, entry.ToString() + existing);
            }
        }
        catch
        {
            // Crash logging must never itself crash the app.
        }
    }

    private static string GetLogFilePath()
    {
        var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory;
        return Path.Combine(exeDirectory, "CrashReports.txt");
    }
}
