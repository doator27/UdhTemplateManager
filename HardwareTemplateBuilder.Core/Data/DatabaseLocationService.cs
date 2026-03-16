using System;
using System.IO;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Reads and writes the per-machine pointer file (<c>db_location.txt</c>) that stores
/// the path to the shared SQLite database. Each machine keeps its own copy of this file
/// in its local <c>AppData</c> folder; the database file itself lives on a network share
/// accessible to all team members.
/// </summary>
public static class DatabaseLocationService
{
    private const string FileName = "db_location.txt";

    /// <summary>
    /// Returns the configured database file path, or <c>null</c> if the pointer file does
    /// not exist or contains only whitespace.
    /// </summary>
    public static string? GetConfiguredPath()
    {
        var filePath = GetPointerFilePath();
        if (!File.Exists(filePath)) return null;

        var stored = File.ReadAllText(filePath).Trim();
        return string.IsNullOrEmpty(stored) ? null : stored;
    }

    /// <summary>
    /// Saves <paramref name="dbPath"/> to the local pointer file so subsequent launches
    /// find the database without prompting.
    /// </summary>
    /// <param name="dbPath">Full path to the SQLite <c>.db</c> file.</param>
    public static void SetConfiguredPath(string dbPath)
    {
        File.WriteAllText(GetPointerFilePath(), dbPath);
    }

    private static string GetPointerFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder  = Path.Combine(appData, "HardwareTemplateBuilder");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, FileName);
    }
}
