using Microsoft.EntityFrameworkCore;
using System;
using System.IO;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Handles creation and migration of the SQLite database on first run and supports
/// moving the database to a user-chosen location via App Settings.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>The application name, used as the subfolder within ApplicationData.</summary>
    private const string AppFolderName = "HardwareTemplateBuilder";

    /// <summary>The SQLite database file name.</summary>
    private const string DatabaseFileName = "hardware_templates.db";

    /// <summary>
    /// Returns the default database path (<c>AppData/HardwareTemplateBuilder/hardware_templates.db</c>).
    /// This is used on first run or whenever no custom location has been configured.
    /// </summary>
    public static string GetDefaultPath()
    {
        var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder     = Path.Combine(appDataFolder, AppFolderName);
        Directory.CreateDirectory(appFolder);
        return Path.Combine(appFolder, DatabaseFileName);
    }

    /// <summary>
    /// Returns the active database file path: the value from <see cref="DatabaseLocationService"/>
    /// if one has been configured via App Settings, otherwise the default local path.
    /// </summary>
    public static string GetDatabasePath() =>
        DatabaseLocationService.GetConfiguredPath() ?? GetDefaultPath();

    /// <summary>
    /// Creates a configured <see cref="AppDbContext"/> pointed at the active database file.
    /// </summary>
    public static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={GetDatabasePath()}")
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Ensures the database at the active path exists, all pending migrations are applied,
    /// and any schema changes that may have been missed by the migration system are patched.
    /// Call this on application startup after the database location has been resolved.
    /// </summary>
    public static void Initialize()
    {
        using var context = CreateContext();
        context.Database.Migrate();
        EnsureSchemaPatches(context);
    }

    /// <summary>
    /// Opens a context at <paramref name="dbPath"/> and applies all pending migrations,
    /// creating the database schema if the file is new.
    /// Used by <c>DatabaseSetupDialog</c> to validate / initialise a chosen path before
    /// committing it to the pointer file.
    /// </summary>
    /// <param name="dbPath">Full path to a SQLite <c>.db</c> file (need not exist yet).</param>
    public static void InitializeAtPath(string dbPath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        using var context = new AppDbContext(options);
        context.Database.Migrate();
        EnsureSchemaPatches(context);
    }

    /// <summary>
    /// Applies schema changes that are required but may not have been picked up by the EF Core
    /// migration runner on existing databases (e.g. manually written migrations whose discovery
    /// attributes were added retroactively). Each patch is idempotent.
    /// </summary>
    private static void EnsureSchemaPatches(AppDbContext context)
    {
        var conn = context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            conn.Open();

        // Patch: MachineId column on UserProfiles (migration 20260315000002)
        if (!ColumnExists(conn, "UserProfiles", "MachineId"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"UserProfiles\" ADD COLUMN \"MachineId\" TEXT";
            cmd.ExecuteNonQuery();
        }
    }

    private static bool ColumnExists(System.Data.Common.DbConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader["name"]?.ToString(), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
