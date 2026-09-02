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
    /// Returns the active database file path.
    /// <para>
    /// When a shared master location has been configured (see
    /// <see cref="DatabaseLocationService"/>), the app always works against this machine's
    /// private local working copy (see <see cref="DatabaseSyncService"/>), which is synced
    /// from/to the master on startup/shutdown. Otherwise, the default local path is used
    /// directly (single-user / no shared location configured).
    /// </para>
    /// </summary>
    public static string GetDatabasePath() =>
        DatabaseLocationService.GetConfiguredPath() != null
            ? DatabaseSyncService.GetLocalWorkingCopyPath()
            : GetDefaultPath();

    /// <summary>
    /// Builds a SQLite connection string for <paramref name="dbPath"/>.
    /// WAL journal mode and busy timeout are applied via PRAGMA in
    /// <see cref="EnsureSchemaPatches"/> — <c>Microsoft.Data.Sqlite</c> does not accept
    /// these as connection string keywords.
    /// </summary>
    private static string BuildConnectionString(string dbPath) =>
        $"Data Source={dbPath}";

    /// <summary>
    /// Creates a configured <see cref="AppDbContext"/> pointed at the active database file.
    /// </summary>
    public static AppDbContext CreateContext()
    {
        var dbPath = GetDatabasePath();
        EnsureWritable(dbPath);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(BuildConnectionString(dbPath))
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Ensures <paramref name="dbPath"/>, its containing folder, and its -journal/-wal/-shm
    /// sidecar files (if present) are not marked read-only.
    /// <para>
    /// This app uses SQLite's DELETE journal mode, which creates a transient
    /// <c>&lt;db&gt;-journal</c> file next to the database for every write transaction. If a
    /// previous crash, antivirus scan, backup tool, or <see cref="File.Copy(string, string, bool)"/>
    /// (which preserves attributes) leaves that sidecar — or the .db file itself — marked
    /// read-only, every subsequent write fails with "attempt to write a readonly database" even
    /// though the folder itself is writable.
    /// </para>
    /// </summary>
    internal static void EnsureWritable(string dbPath)
    {
        var folder = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(folder))
            ClearReadOnlyAttribute(folder);

        ClearReadOnlyAttribute(dbPath);
        ClearReadOnlyAttribute(dbPath + "-journal");
        ClearReadOnlyAttribute(dbPath + "-wal");
        ClearReadOnlyAttribute(dbPath + "-shm");
    }

    private static void ClearReadOnlyAttribute(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
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
        EnsureWritable(dbPath);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(BuildConnectionString(dbPath))
            .AddInterceptors(new SqlitePragmaInterceptor())
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

        // Patch: 5-second busy timeout so operations queue rather than fail immediately.
        using (var busyCmd = conn.CreateCommand())
        {
            busyCmd.CommandText = "PRAGMA busy_timeout=5000;";
            busyCmd.ExecuteNonQuery();
        }

        // Patch: CalloutRemarks
        if (!ColumnExists(conn, "JobHardware", "CalloutRemarks"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"JobHardware\" ADD COLUMN \"CalloutRemarks\" TEXT";
            cmd.ExecuteNonQuery();
        }

        // Patch: Job notes columns (AddJobNotes migration — emptied to handle pre-existing columns).
        if (!ColumnExists(conn, "Jobs", "Notes"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"Jobs\" ADD COLUMN \"Notes\" TEXT";
            cmd.ExecuteNonQuery();
        }
        if (!ColumnExists(conn, "Jobs", "NotesUpdatedAt"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"Jobs\" ADD COLUMN \"NotesUpdatedAt\" TEXT";
            cmd.ExecuteNonQuery();
        }

        // Patch: MachineId column on UserProfiles (migration 20260315000002)
        if (!ColumnExists(conn, "UserProfiles", "MachineId"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"UserProfiles\" ADD COLUMN \"MachineId\" TEXT";
            cmd.ExecuteNonQuery();
        }

        // Patch: OriginJobId on IndividualTemplates (Phase 35 — job-scoped templates)
        if (!ColumnExists(conn, "IndividualTemplates", "OriginJobId"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"IndividualTemplates\" ADD COLUMN \"OriginJobId\" INTEGER REFERENCES \"Jobs\"(\"Id\") ON DELETE SET NULL";
            cmd.ExecuteNonQuery();
        }

        // Patch: JobId on HardwareItemTemplates (Phase 35 — job-scoped template links)
        if (!ColumnExists(conn, "HardwareItemTemplates", "JobId"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"HardwareItemTemplates\" ADD COLUMN \"JobId\" INTEGER REFERENCES \"Jobs\"(\"Id\") ON DELETE SET NULL";
            cmd.ExecuteNonQuery();
        }

        // Patch: IsActive on HardwareItems — ensures all existing items are marked active.
        if (!ColumnExists(conn, "HardwareItems", "IsActive"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE \"HardwareItems\" ADD COLUMN \"IsActive\" INTEGER NOT NULL DEFAULT 1";
            cmd.ExecuteNonQuery();

            // Only set all existing records to active when the column is first added.
            using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = "UPDATE \"HardwareItems\" SET \"IsActive\" = 1 WHERE \"IsActive\" IS NULL";
            updateCmd.ExecuteNonQuery();
        }

        // Patch: SMTP / alert email settings (Phase 24).
        // Read first: only acquire a write lock when rows are actually missing.
        // On a fully-initialised database this reduces 6 write-lock acquisitions to zero,
        // which eliminates the main source of startup lock contention on shared network drives.
        var smtpDefaults = new (string Key, string Value)[]
        {
            ("SmtpHost",       ""),
            ("SmtpPort",       "587"),
            ("SmtpUsername",   ""),
            ("SmtpPassword",   ""),
            ("AlertEmailTo",   ""),
            ("AlertEmailFrom", "")
        };
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText =
                "SELECT COUNT(*) FROM \"AppSettings\" WHERE \"Key\" IN " +
                "('SmtpHost','SmtpPort','SmtpUsername','SmtpPassword','AlertEmailTo','AlertEmailFrom')";
            var existing = (long)(countCmd.ExecuteScalar() ?? 0L);
            if (existing < smtpDefaults.Length)
            {
                using var tx = conn.BeginTransaction();
                foreach (var (key, value) in smtpDefaults)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = $"INSERT OR IGNORE INTO \"AppSettings\" (\"Key\", \"Value\") VALUES ('{key}', '{value}')";
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        // Patch: IgnoredTemplateDuplicates table (migration 20260420120000)
        if (!TableExists(conn, "IgnoredTemplateDuplicates"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE ""IgnoredTemplateDuplicates"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_IgnoredTemplateDuplicates"" PRIMARY KEY AUTOINCREMENT,
                    ""SharedLink"" TEXT NOT NULL,
                    ""LinkType"" TEXT NOT NULL,
                    ""IgnoredAt"" TEXT NOT NULL
                )";
            cmd.ExecuteNonQuery();

            using var idxCmd = conn.CreateCommand();
            idxCmd.CommandText = @"
                CREATE UNIQUE INDEX ""IX_IgnoredTemplateDuplicates_SharedLink_LinkType""
                ON ""IgnoredTemplateDuplicates"" (""SharedLink"", ""LinkType"")";
            idxCmd.ExecuteNonQuery();
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

    private static bool TableExists(System.Data.Common.DbConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@tableName";
        var param = cmd.CreateParameter();
        param.ParameterName = "@tableName";
        param.Value = table;
        cmd.Parameters.Add(param);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }
}
