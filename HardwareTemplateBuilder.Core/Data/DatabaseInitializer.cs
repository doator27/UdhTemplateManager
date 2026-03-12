using Microsoft.EntityFrameworkCore;
using System;
using System.IO;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Handles creation and migration of the SQLite database on first run.
/// The database file is stored in the user's ApplicationData folder.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// The application name, used as the subfolder within ApplicationData.
    /// </summary>
    private const string AppFolderName = "HardwareTemplateBuilder";

    /// <summary>
    /// The SQLite database file name.
    /// </summary>
    private const string DatabaseFileName = "hardware_templates.db";

    /// <summary>
    /// Gets the full path to the SQLite database file.
    /// Creates the application data directory if it does not exist.
    /// </summary>
    /// <returns>The absolute path to the .db file.</returns>
    public static string GetDatabasePath()
    {
        var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appDataFolder, AppFolderName);
        Directory.CreateDirectory(appFolder);
        return Path.Combine(appFolder, DatabaseFileName);
    }

    /// <summary>
    /// Creates a configured <see cref="AppDbContext"/> pointed at the ApplicationData database file.
    /// </summary>
    /// <returns>A ready-to-use <see cref="AppDbContext"/> instance.</returns>
    public static AppDbContext CreateContext()
    {
        var dbPath = GetDatabasePath();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Ensures the database exists and all pending migrations have been applied.
    /// Call this on application startup before any database operations.
    /// </summary>
    public static void Initialize()
    {
        using var context = CreateContext();
        context.Database.Migrate();
    }
}
