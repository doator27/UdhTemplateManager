using Microsoft.Data.Sqlite;
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
    /// Returns the active (local, per-machine) database file path.
    /// <para>
    /// The app always works against this local, per-machine copy of the database
    /// (<see cref="GetDefaultPath"/>). If a master location has been configured (see
    /// <see cref="DatabaseLocationService"/>), it is synced with this local copy on startup via
    /// <see cref="MasterSyncService"/> — new/changed local rows are merged into the master, then
    /// a clean copy of the master is written back over this local file. When the master is
    /// unreachable, the app simply continues working against the existing local copy.
    /// </para>
    /// </summary>
    public static string GetDatabasePath() => GetDefaultPath();

    /// <summary>
    /// Returns the configured master database path (network share / shared location), or
    /// <c>null</c> if none has been configured. Used by <see cref="MasterSyncService"/> to
    /// merge and sync with the local working copy on startup.
    /// </summary>
    public static string? GetMasterPath() => DatabaseLocationService.GetConfiguredPath();

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
    /// Deletes the local (per-machine) database's stale -journal/-wal/-shm sidecar files, which
    /// is the usual cause of a persistent "database is locked"/"attempt to write a readonly
    /// database" failure on the local copy (e.g. a leftover -journal file from a process that
    /// crashed or was force-closed mid-write). Never touches the .db file itself, so it cannot
    /// lose data at rest \u2014 at worst, an in-flight transaction from another process on this same
    /// machine could be interrupted and would simply retry. Never throws; returns a message
    /// describing what was removed (or that there was nothing to clear).
    /// </summary>
    public static string ClearLocalLock()
    {
        var (removed, error) = ClearJournalSidecars(GetDatabasePath());
        if (error != null)
            return error;

        return removed.Count == 0
            ? "No leftover lock/journal files were found on the local database."
            : $"Removed: {string.Join(", ", removed)}. Try again now.";
    }

    /// <summary>
    /// Deletes any leftover -journal/-wal/-shm sidecar files next to <paramref name="dbPath"/>.
    /// Shared by <see cref="ClearLocalLock"/> (local database) and
    /// <see cref="MasterSyncService"/> (master database, once it holds the advisory sync lock).
    /// Never touches the .db file itself, so it cannot lose data at rest. Returns the list of
    /// files actually removed, and a non-null error message if a deletion failed part-way
    /// through (best-effort — earlier removals in the same call still took effect).
    /// </summary>
    internal static (System.Collections.Generic.List<string> Removed, string? Error) ClearJournalSidecars(string dbPath)
    {
        var removed = new System.Collections.Generic.List<string>();

        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
        {
            var path = dbPath + suffix;
            try
            {
                if (File.Exists(path))
                {
                    ClearReadOnlyAttribute(path);
                    File.Delete(path);
                    removed.Add(Path.GetFileName(path));
                }
            }
            catch (Exception ex)
            {
                return (removed, $"Could not remove '{Path.GetFileName(path)}': {ex.Message}");
            }
        }

        return (removed, null);
    }

    /// <summary>
    /// Ensures the database at the active path exists, all pending migrations are applied,
    /// and any schema changes that may have been missed by the migration system are patched.
    /// Call this on application startup after the database location has been resolved.
    /// </summary>
    public static void Initialize()
    {
        // Startup can race with another instance of this app (or a lingering process from a
        // previous crash) still holding a write lock on the local database. Without a retry
        // here, a transient SQLITE_BUSY/SQLITE_LOCKED during migration/schema-patching is an
        // unhandled exception that aborts app startup entirely (see App.OnFrameworkInitializationCompleted),
        // leaving the main window open but empty. Every patch below is idempotent, so retrying
        // the whole sequence is always safe.
        try
        {
            RetryHelper.ExecuteWithRetry(() =>
            {
                using var context = CreateContext();
                context.Database.Migrate();
                EnsureSchemaPatches(context);
            }, maxRetries: 5, baseDelayMs: 500);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            // The retry budget above (~15s of backoff, each attempt also waiting up to the
            // 10s busy_timeout set in EnsureSchemaPatches) was exhausted without the lock
            // clearing. That points to a non-transient lock rather than brief contention —
            // most commonly a stale -journal/-wal/-shm sidecar file left behind by a process
            // that crashed or was force-closed mid-write (see ClearLocalLock). Log the
            // diagnostic detail, attempt exactly one automatic cleanup + retry, and only
            // propagate the failure if that also doesn't resolve it.
            LogSchemaPatchFailure(ex, "initial retry budget exhausted; attempting stale-lock cleanup");

            var cleanupResult = ClearLocalLock();
            LogSchemaPatchFailure(ex, $"ClearLocalLock result: {cleanupResult}");

            try
            {
                RetryHelper.ExecuteWithRetry(() =>
                {
                    using var context = CreateContext();
                    context.Database.Migrate();
                    EnsureSchemaPatches(context);
                }, maxRetries: 2, baseDelayMs: 500);
            }
            catch (SqliteException finalEx) when (finalEx.SqliteErrorCode is 5 or 6)
            {
                LogSchemaPatchFailure(finalEx, "stale-lock cleanup did not resolve the lock; giving up");
                throw;
            }
        }
    }

    /// <summary>
    /// Appends a diagnostic entry (timestamp, SQLite error code, message, and context note) to
    /// "SchemaPatchWarnings.log" next to the active database file. Used to capture detail on
    /// "database is locked" failures during <see cref="Initialize"/> that survive the normal
    /// retry/backoff in <see cref="RetryHelper"/>, without requiring a UI-layer logging
    /// dependency in this Core project. Never throws.
    /// </summary>
    private static void LogSchemaPatchFailure(SqliteException ex, string context)
    {
        try
        {
            var dbPath = GetDatabasePath();
            var folder = Path.GetDirectoryName(dbPath);
            if (string.IsNullOrEmpty(folder))
                return;

            var logPath = Path.Combine(folder, "SchemaPatchWarnings.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | SqliteErrorCode={ex.SqliteErrorCode} | {ex.Message} | {context}{Environment.NewLine}";
            File.AppendAllText(logPath, line);
        }
        catch
        {
            // Diagnostic logging must never itself crash startup.
        }
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

        // See Initialize() — retried for the same reason (transient lock contention on shared
        // network drives/master files is expected here too).
        RetryHelper.ExecuteWithRetry(() =>
        {
            using var context = new AppDbContext(options);
            context.Database.Migrate();
            EnsureSchemaPatches(context);
        }, maxRetries: 5, baseDelayMs: 500);
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

        // Patch: 10-second busy timeout so operations queue rather than fail immediately.
        using (var busyCmd = conn.CreateCommand())
        {
            busyCmd.CommandText = "PRAGMA busy_timeout=10000;";
            busyCmd.ExecuteNonQuery();
        }

        // Patch: force DELETE journal mode. WAL mode relies on shared-memory (-shm) locking
        // that is unreliable over network shares (SMB/mapped drives) \u2014 a database left in WAL
        // mode from an older app version, or opened once with journal_mode=WAL, will
        // intermittently fail SQLite's quick_check (see MasterSyncService.IsIntegrityOk) even
        // though it isn't actually corrupted, causing the master to be needlessly "repaired"
        // from a local copy on every sync. DELETE mode is safe for shared network drives.
        using (var journalCmd = conn.CreateCommand())
        {
            journalCmd.CommandText = "PRAGMA journal_mode=DELETE;";
            journalCmd.ExecuteNonQuery();
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

        // Patch: SyncTombstones table. Records a deletion (table name + encoded primary key)
        // so that RecordDeletionService's force-delete doesn't get silently undone by the next
        // sync: without this, a row deleted from master but still present in some other
        // machine's local database would simply get re-inserted by that machine's next
        // "local -> master" merge, and then copied straight back down to every other local
        // database on their next "master -> local" copy-back. See MasterSyncService.MergeTables
        // (applies tombstones to master before merging local rows in) and
        // MasterSyncService.ApplyTombstonesLocally (applies them to the local db after copy-back).
        if (!TableExists(conn, "SyncTombstones"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE ""SyncTombstones"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_SyncTombstones"" PRIMARY KEY AUTOINCREMENT,
                    ""TableName"" TEXT NOT NULL,
                    ""RecordKey"" TEXT NOT NULL,
                    ""DeletedAt"" TEXT NOT NULL
                )";
            cmd.ExecuteNonQuery();

            using var idxCmd = conn.CreateCommand();
            idxCmd.CommandText = @"
                CREATE UNIQUE INDEX ""IX_SyncTombstones_TableName_RecordKey""
                ON ""SyncTombstones"" (""TableName"", ""RecordKey"")";
            idxCmd.ExecuteNonQuery();
        }

        // Patch: SyncSkipList table. Records a record (table name + encoded primary key) that
        // was deliberately excluded from a "clean" sync because it (or an ancestor) was found
        // corrupted during a merge (see MasterSyncService.LastFailedRecords and
        // CorruptedRecordSkipService). Like SyncTombstones, this table travels with the database
        // and is reconciled bidirectionally on every sync so that every machine permanently
        // stops re-importing the same corrupted records, even ones it never personally
        // encountered a merge failure for.
        if (!TableExists(conn, "SyncSkipList"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE ""SyncSkipList"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_SyncSkipList"" PRIMARY KEY AUTOINCREMENT,
                    ""TableName"" TEXT NOT NULL,
                    ""RecordKey"" TEXT NOT NULL,
                    ""SkippedAt"" TEXT NOT NULL,
                    ""Reason"" TEXT NOT NULL
                )";
            cmd.ExecuteNonQuery();

            using var idxCmd = conn.CreateCommand();
            idxCmd.CommandText = @"
                CREATE UNIQUE INDEX ""IX_SyncSkipList_TableName_RecordKey""
                ON ""SyncSkipList"" (""TableName"", ""RecordKey"")";
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
