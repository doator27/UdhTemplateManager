using System;
using System.IO;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Reads and writes the <c>db_location.txt</c> pointer file that stores the path to the
/// shared SQLite database.
/// <para>
/// Priority order when reading:
/// <list type="number">
///   <item>Exe-directory pointer (<c>{exe folder}/db_location.txt</c>) — shared when the
///     application is run from a network drive.</item>
///   <item>Per-machine AppData pointer — legacy fallback for local installs.</item>
/// </list>
/// When writing, the exe-directory location is used if it is writable; otherwise
/// the write falls back to AppData.
/// </para>
/// </summary>
public static class DatabaseLocationService
{
    private const string FileName = "db_location.txt";

    /// <summary>
    /// Returns the configured database file path, or <c>null</c> if neither pointer file
    /// exists or both contain only whitespace.
    /// </summary>
    public static string? GetConfiguredPath()
    {
        // 1. Exe-directory pointer (shared config — works when exe is on a network drive).
        var exePath = GetExeDirectoryPointerPath();
        if (exePath != null && File.Exists(exePath))
        {
            var stored = File.ReadAllText(exePath).Trim();
            if (!string.IsNullOrEmpty(stored)) return stored;
        }

        // 2. Per-machine AppData pointer (legacy / dev fallback).
        var appDataPath = GetAppDataPointerPath();
        if (File.Exists(appDataPath))
        {
            var stored = File.ReadAllText(appDataPath).Trim();
            if (!string.IsNullOrEmpty(stored))
            {
                // Auto-migrate: promote AppData value to exe-directory if possible.
                TryWriteExeDirectory(stored);
                return stored;
            }
        }

        return null;
    }

    /// <summary>
    /// Saves <paramref name="dbPath"/> to the pointer file. Writes to the exe-directory
    /// location (shared) if writable; otherwise writes to the AppData location.
    /// </summary>
    /// <param name="dbPath">Full path to the SQLite <c>.db</c> file.</param>
    public static void SetConfiguredPath(string dbPath)
    {
        if (!TryWriteExeDirectory(dbPath))
            File.WriteAllText(GetAppDataPointerPath(), dbPath);
    }

    /// <summary>
    /// Returns the path of the pointer file that is currently active, for display in the UI.
    /// </summary>
    public static string GetActivePointerFilePath()
    {
        var exePath = GetExeDirectoryPointerPath();
        if (exePath != null && File.Exists(exePath)) return exePath;
        return GetAppDataPointerPath();
    }

    private static bool TryWriteExeDirectory(string dbPath)
    {
        var exePath = GetExeDirectoryPointerPath();
        if (exePath == null) return false;
        try { File.WriteAllText(exePath, dbPath); return true; }
        catch { return false; }
    }

    private static string? GetExeDirectoryPointerPath()
    {
        var baseDir = AppContext.BaseDirectory;
        return string.IsNullOrEmpty(baseDir) ? null : Path.Combine(baseDir, FileName);
    }

    private static string GetAppDataPointerPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder  = Path.Combine(appData, "HardwareTemplateBuilder");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, FileName);
    }
}
