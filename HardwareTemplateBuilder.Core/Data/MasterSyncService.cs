using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Result of a <see cref="MasterSyncService.Sync"/> attempt.
/// </summary>
public enum MasterSyncResult
{
    /// <summary>No master location is configured — nothing to do.</summary>
    NotConfigured,

    /// <summary>The configured master location could not be reached; local DB left untouched.</summary>
    MasterUnreachable,

    /// <summary>Local rows were merged into master and the local file was refreshed from master.</summary>
    Synced,

    /// <summary>
    /// The master database file failed a SQLite integrity check and was left untouched —
    /// merging was skipped entirely rather than overwriting it. This is intentionally
    /// non-destructive: with only a handful of users sharing the master, silently rebuilding it
    /// from whichever machine happens to sync next would discard everyone else's data. Restore
    /// the master from a known-good backup (see DatabaseBackupService) before syncing again.
    /// </summary>
    MasterIntegrityCheckFailed,

    /// <summary>An unexpected error occurred; local DB left untouched (best-effort, non-fatal).</summary>
    Failed
}

/// <summary>
/// Synchronizes the per-machine local SQLite database with a shared "master" SQLite database
/// on startup.
/// <para>
/// The flow is: merge any new or changed rows from the local working copy into the master
/// (matched by primary key — missing rows are inserted, existing rows are overwritten with the
/// local values), then overwrite the local file with a clean copy of the now-merged master so
/// both are byte-identical going forward. All application code continues to read/write the
/// local file afterward (see <see cref="DatabaseInitializer.GetDatabasePath"/>).
/// </para>
/// <para>
/// If the master is unreachable (e.g. network share offline), the sync is skipped entirely and
/// the app continues working offline against the existing local copy; the sync is retried on
/// the next startup.
/// </para>
/// </summary>
public static class MasterSyncService
{
    /// <summary>
    /// The exception message from the most recent <see cref="MasterSyncResult.Failed"/> result,
    /// or <c>null</c> if the last attempt did not fail. Intended for diagnostics/UI display only
    /// — <see cref="Sync"/> itself never throws.
    /// </summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// Attempts to sync <paramref name="localPath"/> with <paramref name="masterPath"/>.
    /// Never throws — all failures are swallowed and reported via the return value so that
    /// startup can always continue against the local database.
    /// </summary>
    /// <param name="masterPath">Configured master database path, or <c>null</c> if none set.</param>
    /// <param name="localPath">The local, per-machine working database path.</param>
    public static MasterSyncResult Sync(string? masterPath, string localPath)
    {
        LastError = null;

        if (string.IsNullOrWhiteSpace(masterPath))
            return MasterSyncResult.NotConfigured;

        try
        {
            if (!File.Exists(masterPath))
                return MasterSyncResult.MasterUnreachable;

            // Ensure the master schema is up to date before merging into it.
            DatabaseInitializer.InitializeAtPath(masterPath);

            DatabaseInitializer.EnsureWritable(masterPath);
            DatabaseInitializer.EnsureWritable(localPath);

            // Multiple machines may start up around the same time and race to write to the
            // shared master file. Serialize the merge across processes with a simple advisory
            // lock file so only one machine merges/checkpoints master at a time; other machines
            // skip this attempt and retry on their next launch rather than colliding.
            using var masterLock = MasterFileLock.TryAcquire(masterPath);
            if (masterLock == null)
                return MasterSyncResult.MasterUnreachable;

            if (!IsIntegrityOk(masterPath, out var masterCheckDetail))
            {
                // The master failed SQLite's integrity check. With only a handful of users
                // sharing this file, automatically overwriting it from whichever machine
                // happens to sync next is destructive — it would silently discard everyone
                // else's data. Skip the merge and leave both the master and this machine's
                // local copy untouched; a human should restore the master from a known-good
                // backup (see DatabaseBackupService) before syncing again.
                LastError =
                    $"Master database at '{masterPath}' failed SQLite's integrity check. " +
                    "Sync was skipped to avoid overwriting it — restore the master from a backup, then sync again. " +
                    $"Details: {masterCheckDetail}";
                return MasterSyncResult.MasterIntegrityCheckFailed;
            }

            MergeLocalIntoMaster(masterPath, localPath);
            CopyMasterToLocal(masterPath, localPath);

            return MasterSyncResult.Synced;
        }
        catch (Exception ex)
        {
            // Best-effort: any failure means we simply keep working against the existing
            // local copy and try again next launch. The message is captured for diagnostics.
            LastError = ex.ToString();
            return MasterSyncResult.Failed;
        }
    }

    /// <summary>
    /// Merges every row from the local database into the attached master database, table by
    /// table, using an upsert keyed on each entity's primary key columns. Foreign key
    /// enforcement is disabled for the duration of the merge to avoid cross-table insert-order
    /// failures (e.g. self-referential hierarchies), then re-checked afterward (violations are
    /// swallowed — this is a best-effort merge).
    /// </summary>
    private static void MergeLocalIntoMaster(string masterPath, string localPath)
    {
        var tables = GetMappedTables();

        using var connection = new SqliteConnection($"Data Source={localPath}");
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            // Waits up to 10s for locks (held by other local readers/writers or by the
            // network share) instead of failing immediately with SQLITE_BUSY/SQLITE_LOCKED.
            pragma.CommandText = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=OFF;";
            pragma.ExecuteNonQuery();
        }

        using (var attach = connection.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $masterPath AS master;";
            attach.Parameters.AddWithValue("$masterPath", masterPath);
            attach.ExecuteNonQuery();
        }

        try
        {
            RetryHelper.ExecuteWithRetry(() => MergeTables(connection, tables), maxRetries: 5, baseDelayMs: 300);
        }
        finally
        {
            using var detach = connection.CreateCommand();
            detach.CommandText = "DETACH DATABASE master;";
            detach.ExecuteNonQuery();
        }
    }

    private static void MergeTables(SqliteConnection connection, IReadOnlyList<TableInfo> tables)
    {
        {
            using var transaction = connection.BeginTransaction();

            foreach (var table in tables)
            {
                var columnList = string.Join(", ", table.Columns.Select(c => $"\"{c}\""));
                var updateColumns = table.Columns.Except(table.PrimaryKey).ToList();

                string sql;
                if (updateColumns.Count == 0)
                {
                    // Every column is part of the primary key — nothing to update, just insert missing rows.
                    sql = $"""
                        INSERT OR IGNORE INTO master."{table.Name}" ({columnList})
                        SELECT {columnList} FROM main."{table.Name}";
                        """;
                }
                else
                {
                    // "INSERT OR REPLACE" is used instead of "ON CONFLICT(pk) DO UPDATE" because
                    // some tables (e.g. AppSettings) have additional UNIQUE indexes beyond the
                    // primary key; ON CONFLICT only resolves conflicts on the named columns, so a
                    // row that collides on a different unique index would still fail. REPLACE
                    // resolves a conflict on ANY unique/primary-key index by deleting the
                    // conflicting row first, then inserting the local row's values — matching the
                    // "overwrite existing rows with local values" merge semantics for every table.
                    sql = $"""
                        INSERT OR REPLACE INTO master."{table.Name}" ({columnList})
                        SELECT {columnList} FROM main."{table.Name}";
                        """;
                }

                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();

            // Best-effort consistency check; violations are logged-and-ignored rather than
            // aborting the sync, since this is a best-effort merge.
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA master.foreign_key_check;";
            using var reader = checkCmd.ExecuteReader();
            // Intentionally not surfaced further — merge already committed.
            while (reader.Read()) { }
        }
    }

    /// <summary>
    /// Overwrites <paramref name="localPath"/> with a byte-for-byte copy of
    /// <paramref name="masterPath"/>, clearing any stale WAL/journal sidecar files so the copy
    /// loads cleanly.
    /// </summary>
    private static void CopyMasterToLocal(string masterPath, string localPath)
    {
        SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            TryDelete(localPath + suffix);
        }

        File.Copy(masterPath, localPath, overwrite: true);
        DatabaseInitializer.EnsureWritable(localPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Non-fatal — a stale sidecar left behind will be superseded on next open.
        }
    }

    /// <summary>
    /// Runs SQLite's <c>quick_check</c> against <paramref name="dbPath"/> and returns
    /// <c>false</c> if the file is physically corrupted (e.g. damaged pages from an unsafe copy,
    /// crash, or network share fault). A healthy database reports a single row containing "ok".
    /// On failure, <paramref name="detail"/> contains every diagnostic line SQLite reported (not
    /// just the fact that it failed) so the real cause can be surfaced to the user instead of a
    /// generic "corrupted" message.
    /// </summary>
    private static bool IsIntegrityOk(string dbPath, out string detail)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        // Wait for locks instead of throwing immediately \u2014 a database that's merely busy
        // (e.g. still being written to by this same process) must not be mistaken for corrupt.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=10000;";
            pragma.ExecuteNonQuery();
        }

        using var cmd = connection.CreateCommand();
        // quick_check is much faster than the full integrity_check and is sufficient to detect
        // the "database disk image is malformed" failure mode this guards against. The (100)
        // argument caps how many errors it reports, not whether it runs \u2014 unlike quick_check(1),
        // it doesn't stop after the very first line, so we can see the full picture.
        cmd.CommandText = "PRAGMA quick_check(100);";
        using var reader = cmd.ExecuteReader();

        var lines = new List<string>();
        while (reader.Read())
            lines.Add(reader.GetString(0));

        if (lines.Count == 1 && string.Equals(lines[0], "ok", StringComparison.OrdinalIgnoreCase))
        {
            detail = "ok";
            return true;
        }

        detail = lines.Count == 0 ? "quick_check returned no rows" : string.Join(" | ", lines);
        return false;
    }

    /// <summary>
    /// Explicitly, manually overwrites the master database at <paramref name="masterPath"/> with
    /// a copy of this machine's local database at <paramref name="localPath"/>. Unlike the
    /// automatic sync flow, this is never called implicitly \u2014 it must be invoked deliberately by
    /// the user (e.g. after confirming a <see cref="MasterSyncResult.MasterIntegrityCheckFailed"/>
    /// result and restoring being impractical) since it discards whatever is currently in master.
    /// </summary>
    /// <param name="masterPath">Configured master database path.</param>
    /// <param name="localPath">This machine's local, per-machine working database path.</param>
    public static void RebuildMasterFromLocal(string masterPath, string localPath)
    {
        LastError = null;
        try
        {
            // Force the local database to checkpoint any WAL frames into its main file and
            // switch to DELETE journal mode so the copy below grabs a complete, self-contained
            // snapshot. Without this, a local file left in WAL mode (or with an active writer)
            // could be copied mid-transaction, producing a "master" that fails its own
            // integrity check immediately \u2014 the exact symptom this guards against.
            DatabaseInitializer.InitializeAtPath(localPath);
            SqliteConnection.ClearAllPools();

            if (!IsIntegrityOk(localPath, out var localCheckDetail))
            {
                LastError =
                    $"This machine's own local database at '{localPath}' failed SQLite's integrity " +
                    "check, so it cannot be used to rebuild the master. Restore this machine's local " +
                    "database from a backup first (see App Settings > Database Backups), then try again. " +
                    $"Details: {localCheckDetail}";
                return;
            }

            using var masterLock = MasterFileLock.TryAcquire(masterPath);
            if (masterLock == null)
            {
                LastError = $"Could not acquire the master lock for '{masterPath}' \u2014 another machine may be syncing right now. Try again shortly.";
                return;
            }

            SqliteConnection.ClearAllPools();

            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                TryDelete(masterPath + suffix);
            }

            File.Copy(localPath, masterPath, overwrite: true);
            DatabaseInitializer.EnsureWritable(masterPath);

            SqliteConnection.ClearAllPools();
            if (!IsIntegrityOk(masterPath, out var postRebuildDetail))
            {
                LastError =
                    $"Master database at '{masterPath}' still fails SQLite's integrity check " +
                    "immediately after being rebuilt. This points to a problem writing to that path " +
                    "itself (e.g. antivirus interference, a flaky network share, or insufficient disk " +
                    $"space) rather than the source data \u2014 check the share/drive and try again. Details: {postRebuildDetail}";
            }
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
        }
    }

    private sealed record TableInfo(string Name, IReadOnlyList<string> Columns, IReadOnlyList<string> PrimaryKey);

    /// <summary>
    /// Reads table name, column, and primary key metadata from the EF model so the merge stays
    /// in sync with the schema without needing to hardcode table lists.
    /// </summary>
    private static IReadOnlyList<TableInfo> GetMappedTables()
    {
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options);

        var result = new List<TableInfo>();
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName == null)
                continue;

            var primaryKey = entityType.FindPrimaryKey();
            if (primaryKey == null)
                continue;

            var pkColumns = primaryKey.Properties.Select(p => p.GetColumnName()!).ToList();
            var allColumns = entityType.GetProperties().Select(p => p.GetColumnName()!).ToList();

            result.Add(new TableInfo(tableName, allColumns, pkColumns));
        }

        return result;
    }

    /// <summary>
    /// Cross-process advisory lock (a <c>.lock</c> sidecar file held open exclusively) used to
    /// ensure only one machine at a time merges into and checkpoints the shared master
    /// database. Without this, several machines starting up simultaneously would all open
    /// write transactions against the same master file on the network share at once, which is
    /// the primary cause of lock-contention crashes under concurrent startup.
    /// </summary>
    private sealed class MasterFileLock : IDisposable
    {
        private readonly FileStream _stream;

        private MasterFileLock(FileStream stream) => _stream = stream;

        /// <summary>
        /// Attempts to acquire the lock for <paramref name="masterPath"/>, retrying for up to
        /// ~15 seconds. Returns <c>null</c> if another machine currently holds it, so the caller
        /// can skip this sync attempt and retry on the next launch instead of blocking startup.
        /// </summary>
        public static MasterFileLock? TryAcquire(string masterPath)
        {
            var lockPath = masterPath + ".lock";
            const int maxAttempts = 30;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    var stream = new FileStream(
                        lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new MasterFileLock(stream);
                }
                catch (IOException)
                {
                    // Another process holds the lock — wait and retry.
                    Thread.Sleep(500);
                }
                catch (UnauthorizedAccessException)
                {
                    // Lock file itself is inaccessible (permissions, read-only share, etc.) —
                    // treat the master as unreachable for this attempt.
                    return null;
                }
            }

            return null;
        }

        public void Dispose() => _stream.Dispose();
    }
}
