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
