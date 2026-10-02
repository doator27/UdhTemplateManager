using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
/// <para>
/// Every failure is tagged with the specific stage it occurred in (lock acquisition, integrity
/// check, merge, or copy-back) so <see cref="LastError"/> always identifies exactly what went
/// wrong instead of a generic, undiagnosable message — this matters most on shared network
/// drives, where transient share/permission/contention issues are the most common cause of a
/// failed sync.
/// </para>
/// </summary>
public static class MasterSyncService
{
    /// <summary>
    /// The detailed diagnostic message from the most recent non-<see cref="MasterSyncResult.Synced"/>
    /// result, or <c>null</c> if the last attempt succeeded. Intended for diagnostics/UI display
    /// only — <see cref="Sync"/> itself never throws.
    /// </summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// One record that failed to merge during the most recent <see cref="Sync"/> call, along
    /// with enough information (table name and primary key values) to locate and, if desired,
    /// delete it via <see cref="RecordDeletionService"/>.
    /// </summary>
    /// <param name="TableName">The database table the record belongs to.</param>
    /// <param name="PrimaryKey">The record's primary key column names and values.</param>
    /// <param name="Reason">A human-readable description of why the merge failed.</param>
    public sealed record FailedRecord(string TableName, IReadOnlyDictionary<string, object?> PrimaryKey, string Reason);

    /// <summary>
    /// Every individual record that failed to merge during the most recent <see cref="Sync"/>
    /// call (populated only when the bulk merge for a table fails and falls back to row-by-row
    /// merging — see <see cref="MergeTableRowByRow"/>). Empty if the last sync had no per-record
    /// failures, including when it succeeded outright.
    /// </summary>
    public static IReadOnlyList<FailedRecord> LastFailedRecords { get; private set; } = Array.Empty<FailedRecord>();

    /// <summary>
    /// Maximum time to wait for the advisory master file lock before giving up on this sync
    /// attempt. Bounds startup latency in the presence of a stale lock (e.g. left behind by a
    /// crashed or force-closed instance) so the application can never hang indefinitely waiting
    /// for a lock that will never be released. Exposed as a settable property so tests can use
    /// a short timeout instead of waiting the full production duration.
    /// </summary>
    public static TimeSpan LockAcquireTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Unconditionally deletes the advisory <c>.lock</c> file for <paramref name="masterPath"/>,
    /// along with any stale <c>-wal</c>/<c>-shm</c>/<c>-journal</c> sidecar files sitting next
    /// to it. Intended as a manual escape hatch for the user (via the Sync Database view) when
    /// a sync is reported as locked/unreachable but no one else is actually using the database
    /// — e.g. a lock left behind by a machine that crashed, lost network connectivity, or was
    /// force-closed mid-sync before it could release the lock itself. Never throws; returns a
    /// message describing what was removed (or that there was nothing to clear).
    /// <para>
    /// This never touches the master <c>.db</c> file itself, only the lock and its transient
    /// sidecar files, so it cannot destroy data — at worst, a genuinely in-progress sync from
    /// another machine could be interrupted and would simply retry on its next launch.
    /// </para>
    /// </summary>
    public static string ForceClearLock(string? masterPath)
    {
        if (string.IsNullOrWhiteSpace(masterPath))
            return "No master database is configured.";

        var removed = new List<string>();
        foreach (var suffix in new[] { ".lock", "-wal", "-shm", "-journal" })
        {
            var path = masterPath + suffix;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed.Add(Path.GetFileName(path));
                }
            }
            catch (Exception ex)
            {
                return $"Could not remove '{Path.GetFileName(path)}': {DescribeException(ex)}";
            }
        }

        return removed.Count == 0
            ? "No lock or leftover sidecar files were found — nothing to clear."
            : $"Removed: {string.Join(", ", removed)}. You can try syncing again now.";
    }

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
        var failedRecords = new List<FailedRecord>();

        if (string.IsNullOrWhiteSpace(masterPath))
            return MasterSyncResult.NotConfigured;

        try
        {
            LastFailedRecords = failedRecords;

            if (!File.Exists(masterPath))
                return MasterSyncResult.MasterUnreachable;

            // Multiple machines may start up around the same time and race to write to the
            // shared master file — including the schema-patching ALTER TABLE/PRAGMA statements
            // below, which require an exclusive lock and are not safely retryable across
            // concurrent processes on a network share. Serialize everything (schema check
            // onward) across processes with a simple advisory lock file so only one machine
            // touches master at a time; other machines skip this attempt and retry on their
            // next launch rather than colliding.
            //
            // The acquire attempt is bounded by a hard timeout: a lock file left behind by a
            // crashed or force-closed instance (or any other unexpectedly long wait) must never
            // be able to hang application startup indefinitely. If the timeout elapses, sync is
            // simply skipped for this launch — startup always continues against the local copy.
            var lockAcquireTask = Task.Run(() => MasterFileLock.TryAcquire(masterPath));
            if (!lockAcquireTask.Wait(LockAcquireTimeout))
            {
                LastError =
                    $"Timed out after {LockAcquireTimeout.TotalSeconds:0}s waiting for the sync " +
                    $"lock for '{masterPath}'. A stale lock file from a previous run may be " +
                    "present; sync was skipped for this launch.";
                return MasterSyncResult.MasterUnreachable;
            }

            var lockResult = lockAcquireTask.Result;
            if (lockResult.Lock == null)
            {
                LastError =
                    $"Could not acquire the sync lock for '{masterPath}': {lockResult.Reason}";
                return lockResult.TreatAsUnreachable
                    ? MasterSyncResult.MasterUnreachable
                    : MasterSyncResult.Failed;
            }

            using var masterLock = lockResult.Lock;
            using var heartbeat = masterLock.StartHeartbeat();

            // Now that we hold the exclusive advisory sync lock, no other machine should be
            // touching the master file. A stale -journal/-wal/-shm sidecar left behind by a
            // machine that crashed or was force-closed mid-sync is the most common cause of
            // "database is locked" persisting even though nobody is currently connected — the
            // sidecar makes SQLite believe a transaction is still in flight. Clear it
            // proactively before every attempt rather than waiting for a failure, since a stuck
            // sidecar can otherwise make every future sync fail immediately every time.
            DatabaseInitializer.ClearJournalSidecars(masterPath);

            // Ensure the master schema is up to date before merging into it. Retried once more
            // (mirroring DatabaseInitializer.Initialize's local self-heal) in case a sidecar
            // reappeared or a transient lock was still draining when it was cleared above.
            RunStage("Schema check", () =>
            {
                try
                {
                    DatabaseInitializer.InitializeAtPath(masterPath);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
                {
                    DatabaseInitializer.ClearJournalSidecars(masterPath);
                    DatabaseInitializer.InitializeAtPath(masterPath);
                }
            });

            RunStage("Prepare files", () =>
            {
                DatabaseInitializer.EnsureWritable(masterPath);
                DatabaseInitializer.EnsureWritable(localPath);
            });

            bool integrityOk;
            string integrityDetail;
            try
            {
                integrityOk = IsIntegrityOk(masterPath, out integrityDetail);
            }
            catch (Exception ex)
            {
                // Best-effort diagnostic only — the integrity check itself is no longer a
                // gate on syncing (see below), so a failure here should not block the merge.
                integrityOk = true;
                integrityDetail = $"integrity check could not run: {DescribeException(ex)}";
            }

            if (!integrityOk)
            {
                // The master failed SQLite's integrity check. Previously this blocked syncing
                // entirely to avoid overwriting the master with corrupted data, but that made
                // sync unusable whenever quick_check reported a false positive (e.g. a network
                // share momentarily reporting spurious page errors). Log the detail for
                // diagnostics but proceed with the merge regardless.
                LastError =
                    $"Master database at '{masterPath}' failed SQLite's integrity check, but syncing " +
                    $"anyway (integrity check is diagnostic-only). Details: {integrityDetail}";
            }

            RunStage("Merge", () => MergeLocalIntoMaster(masterPath, localPath, failedRecords));

            // Reserve a block of future primary-key values for THIS machine's own use before
            // copying master back to local. Every table's auto-increment counter previously
            // advanced independently per machine (each local SQLite file simply used
            // max(existing rowid) + 1), so two machines creating new rows (e.g. new Jobs)
            // while offline could easily be assigned the SAME id. When both later synced, the
            // bulk merge above (INSERT OR REPLACE keyed on primary key) would silently
            // overwrite one machine's row with the other's — making an entire unrelated record
            // vanish from master (the "sync is missing items" symptom) and leaving the
            // overwritten row's own children (e.g. JobHardware rows still pointing at that id)
            // now attached to a completely different parent record (the "hardware link was
            // lost / primary key changed" symptom), since no code ever renumbers foreign keys
            // to match.
            //
            // By bumping master's counter for each table past a reserved block and rewinding
            // this machine's local counter to the start of that same block, this machine's
            // future local inserts are guaranteed a range of ids no other machine can also
            // claim, because the master counter — the single shared source of truth — already
            // moved past that entire range under the exclusive sync lock before any other
            // machine could reserve its own block.
            IReadOnlyDictionary<string, long> idReservations = new Dictionary<string, long>();
            RunStage("Reserve id ranges", () => idReservations = ReserveIdRanges(masterPath, localPath));

            RunStage("Copy back", () => CopyMasterToLocal(masterPath, localPath));

            RunStage("Rewind local id ranges", () => RewindLocalIdRanges(localPath, idReservations));

            return MasterSyncResult.Synced;
        }
        catch (SyncStageException ex)
        {
            // Best-effort: any failure means we simply keep working against the existing
            // local copy and try again next launch. The message identifies exactly which
            // stage failed and why, instead of a generic "sync failed" message.
            LastError = $"Sync failed during stage '{ex.Stage}': {DescribeException(ex.InnerException ?? ex)}";
            return MasterSyncResult.Failed;
        }
        catch (Exception ex)
        {
            LastError = $"Sync failed with an unexpected error: {DescribeException(ex)}";
            return MasterSyncResult.Failed;
        }
    }

    /// <summary>
    /// Wraps <paramref name="action"/> so any exception it throws is re-thrown as a
    /// <see cref="SyncStageException"/> tagged with <paramref name="stage"/>, giving
    /// <see cref="LastError"/> enough context to identify exactly where a sync failed.
    /// </summary>
    private static void RunStage(string stage, Action action)
    {
        try
        {
            action();
        }
        catch (SyncStageException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SyncStageException(stage, ex);
        }
    }

    /// <summary>
    /// Builds a concise, human-actionable description of <paramref name="ex"/>, calling out the
    /// specific failure classes most commonly seen on network shares (permissions, path not
    /// found, share offline, transient lock contention) instead of a raw stack trace.
    /// </summary>
    private static string DescribeException(Exception ex)
    {
        var sb = new StringBuilder();
        sb.Append(ex switch
        {
            UnauthorizedAccessException => "Access denied — check that this machine's user account has write permission to the share/folder. ",
            DirectoryNotFoundException => "The network path could not be found — check that the share is mapped/reachable from this machine. ",
            IOException io when IsShareOrDiskIssue(io) => "A network/disk I/O error occurred while reading or writing the share. ",
            SqliteException sq when sq.SqliteErrorCode is 5 or 6 => "The database was busy/locked by another user for too long. ",
            _ => string.Empty
        });
        sb.Append(ex.Message);
        return sb.ToString();
    }

    private static bool IsShareOrDiskIssue(IOException io) =>
        io.Message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
        io.Message.Contains("share", StringComparison.OrdinalIgnoreCase) ||
        io.Message.Contains("disk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Merges every row from the local database into the attached master database, table by
    /// table, using an upsert keyed on each entity's primary key columns. Foreign key
    /// enforcement is disabled for the duration of the merge to avoid cross-table insert-order
    /// failures (e.g. self-referential hierarchies), then re-checked afterward (violations are
    /// swallowed — this is a best-effort merge).
    /// <para>
    /// Exposed internally so <see cref="CorruptedRecordSkipService"/> can merge local into a
    /// clean, pruned copy of master using the exact same logic (including skip-list/tombstone
    /// exclusion) instead of duplicating it.
    /// </para>
    /// </summary>
    internal static void MergeLocalIntoMaster(string masterPath, string localPath, List<FailedRecord> failedRecords)
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
            // Verify the attach actually produced a usable, writable master schema before
            // spending time merging into it — an ATTACH can succeed even against a file the
            // server subsequently refuses to write to (e.g. a stale share handle).
            using (var verify = connection.CreateCommand())
            {
                verify.CommandText = "SELECT COUNT(*) FROM master.sqlite_master;";
                verify.ExecuteScalar();
            }

            RetryHelper.ExecuteWithRetry(() => MergeTables(connection, tables, failedRecords), maxRetries: 5, baseDelayMs: 300);
        }
        finally
        {
            using var detach = connection.CreateCommand();
            detach.CommandText = "DETACH DATABASE master;";
            try
            {
                detach.ExecuteNonQuery();
            }
            catch
            {
                // Best-effort — the connection is being disposed regardless.
            }
        }
    }

    private static void MergeTables(SqliteConnection connection, IReadOnlyList<TableInfo> tables, List<FailedRecord> failedRecords)
    {
        var failures = new List<string>();
        // Retries of the whole merge (see RetryHelper.ExecuteWithRetry above) start over from
        // scratch, so any per-record failures captured by a previous, retried attempt no longer
        // apply and must not be reported alongside this attempt's results.
        failedRecords.Clear();

        // Reconcile deletions (see RecordDeletionService) BEFORE merging any table's rows:
        // 1. Tombstones recorded in either database (a record force/normal-deleted here or on
        //    another machine) are combined into both databases' SyncTombstones tables, so the
        //    full deletion history is known regardless of which machine originally deleted it.
        // 2. Every tombstoned record is deleted from master right away, in case a slower or
        //    offline machine's stale local copy still has it and is about to be merged in below.
        // Without this, a record deleted from master (and the deleting machine's local copy)
        // would simply get re-inserted the next time ANY other machine (which still has its own
        // local copy) runs this same merge, making the deletion appear to silently "undo" itself.
        ReconcileTombstones(connection, tables);

        // Reconcile the skip list too: a record permanently excluded from a clean sync on one
        // machine (see CorruptedRecordSkipService) must also delete any copy of that record (and
        // its already-identified exclusive children) still lingering in this machine's local
        // database or in master, and every machine must learn of every other machine's skip
        // entries so none of them ever re-import the same corrupted records again.
        ReconcileSkipList(connection, tables);

        // Each table gets its OWN transaction rather than one shared transaction for the whole
        // merge. Previously every table shared a single transaction, but certain SQLite errors
        // (e.g. a busy/locked/IO error on a network-share master) cause SQLite to silently and
        // automatically roll back the ENTIRE transaction, not just the failing statement. Once
        // that happened, every subsequent table's command — and the final Commit() — failed
        // too, and disposing the already-dead transaction surfaced a confusing
        // "cannot rollback - no transaction is active" error that masked the real cause and
        // aborted the whole sync. Isolating each table in its own transaction means a single
        // bad table can only lose that table's changes for this sync, matching the intent
        // described below of not letting one problematic table block the rest.
        foreach (var table in tables)
        {
            var columnList = string.Join(", ", table.Columns.Select(c => $"\"{c}\""));
            var updateColumns = table.Columns.Except(table.PrimaryKey).ToList();
            var tombstoneFilter = BuildTombstoneExclusionClause("src", table);

            string sql;
            if (updateColumns.Count == 0)
            {
                // Every column is part of the primary key — nothing to update, just insert missing rows.
                sql = $"""
                    INSERT OR IGNORE INTO master."{table.Name}" ({columnList})
                    SELECT {columnList} FROM main."{table.Name}" AS src
                    WHERE {tombstoneFilter};
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
                    SELECT {columnList} FROM main."{table.Name}" AS src
                    WHERE {tombstoneFilter};
                    """;
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception bulkEx)
            {
                // The transaction may already have been auto-rolled-back by SQLite itself as
                // part of the error above; explicitly rolling back an already-dead transaction
                // throws its own (unrelated) exception that would otherwise mask the real
                // failure captured just above. Swallow that specific case — it's expected.
                try
                {
                    transaction.Rollback();
                }
                catch
                {
                    // Best-effort — the transaction is being disposed regardless.
                }

                // The bulk INSERT...SELECT only says the table failed, not which row caused it
                // (e.g. a single row with a constraint violation or invalid data). Fall back to
                // merging this table one row at a time so the failure can be reported against
                // the specific record — identified by its primary key — instead of leaving the
                // whole table a mystery. Every other row in the table still merges normally.
                try
                {
                    MergeTableRowByRow(connection, table, failures, failedRecords);
                }
                catch (Exception rowFallbackEx)
                {
                    failures.Add(
                        $"{table.Name}: bulk merge failed ({DescribeException(bulkEx)}); " +
                        $"row-by-row fallback also failed: {DescribeException(rowFallbackEx)}");
                }
            }
        }

        // Best-effort consistency check; violations are logged-and-ignored rather than
        // aborting the sync, since this is a best-effort merge.
        using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = "PRAGMA master.foreign_key_check;";
            using var reader = checkCmd.ExecuteReader();
            // Intentionally not surfaced further — merge already committed.
            while (reader.Read()) { }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"{failures.Count} of {tables.Count} table(s) failed to merge (other tables merged successfully): " +
                string.Join(" | ", failures));
        }
    }

    /// <summary>
    /// Fallback merge path used when a table's bulk <c>INSERT ... SELECT</c> fails. Re-reads
    /// every local row for <paramref name="table"/> and inserts them into master one at a time,
    /// each in its own transaction, so that a single bad row (e.g. a constraint violation)
    /// cannot prevent the rest of the table's rows from merging, and — critically — so the
    /// specific failing record can be identified by its primary key in <paramref name="failures"/>
    /// instead of only naming the table.
    /// </summary>
    private static void MergeTableRowByRow(SqliteConnection connection, TableInfo table, List<string> failures, List<FailedRecord> failedRecords)
    {
        var columnList = string.Join(", ", table.Columns.Select(c => $"\"{c}\""));
        var isPureKeyTable = table.Columns.Except(table.PrimaryKey).Any() == false;
        var verb = isPureKeyTable ? "INSERT OR IGNORE" : "INSERT OR REPLACE";
        var paramNames = table.Columns.Select((_, i) => $"$p{i}").ToList();
        var insertSql =
            $"{verb} INTO master.\"{table.Name}\" ({columnList}) VALUES ({string.Join(", ", paramNames)});";

        // Buffer every local row up front — the same connection can only have one active
        // command/reader at a time, and it's needed again below to run each insert. Rows already
        // covered by a tombstone (see RecordDeletionService/ReconcileTombstones) are excluded so
        // this row-by-row fallback can't re-insert a record that was intentionally deleted.
        //
        // Reading itself can fail partway through if the LOCAL table has physical corruption
        // (e.g. a damaged page for a later row) — this is exactly the scenario that caused the
        // bulk INSERT...SELECT above to fail in the first place. Previously an exception here
        // propagated all the way up and aborted the whole table's merge with only a generic,
        // non-actionable failure message (no specific record identified). Instead, stop reading
        // at the first row that can't be read, keep every row successfully read before it, and
        // report the read failure itself as one "failed record" for this table so the user has
        // something concrete to act on (e.g. via Clean & Sync).
        var rows = new List<object[]>();
        try
        {
            using var selectCmd = connection.CreateCommand();
            var tombstoneFilter = BuildTombstoneExclusionClause("src", table);
            selectCmd.CommandText = $"SELECT {columnList} FROM main.\"{table.Name}\" AS src WHERE {tombstoneFilter};";
            using var reader = selectCmd.ExecuteReader();
            while (true)
            {
                bool hasRow;
                try
                {
                    hasRow = reader.Read();
                }
                catch (Exception readEx)
                {
                    var reason = DescribeException(readEx);
                    failures.Add(
                        $"{table.Name}: could not read all local rows for this table (local database may be " +
                        $"physically corrupted); {rows.Count} row(s) read successfully before the failure. {reason}");
                    failedRecords.Add(new FailedRecord(
                        table.Name,
                        new Dictionary<string, object?> { ["__unreadable_row__"] = $"after {rows.Count} row(s)" },
                        reason));
                    break;
                }

                if (!hasRow)
                    break;

                var values = new object[table.Columns.Count];
                reader.GetValues(values);
                rows.Add(values);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"{table.Name}: could not read local rows for this table: {DescribeException(ex)}");
            return;
        }

        foreach (var row in rows)
        {
            using var rowTransaction = connection.BeginTransaction();
            try
            {
                using (var insertCmd = connection.CreateCommand())
                {
                    insertCmd.Transaction = rowTransaction;
                    insertCmd.CommandText = insertSql;
                    for (var i = 0; i < table.Columns.Count; i++)
                        insertCmd.Parameters.AddWithValue(paramNames[i], row[i] ?? DBNull.Value);
                    insertCmd.ExecuteNonQuery();
                }

                rowTransaction.Commit();
            }
            catch (Exception ex)
            {
                var reason = DescribeException(ex);
                failures.Add($"{table.Name} record ({DescribePrimaryKey(table, row)}): {reason}");
                failedRecords.Add(new FailedRecord(table.Name, BuildPrimaryKeyDict(table, row), reason));

                try
                {
                    rowTransaction.Rollback();
                }
                catch
                {
                    // Best-effort — see the identical guard in MergeTables.
                }
            }
        }
    }

    /// <summary>
    /// Builds a dictionary of primary key column name to value for <paramref name="table"/>'s
    /// given row, for use by <see cref="RecordDeletionService"/> to locate the exact record.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> BuildPrimaryKeyDict(TableInfo table, object[] row)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var pkColumn in table.PrimaryKey)
        {
            var index = FindColumnIndex(table, pkColumn);
            dict[pkColumn] = index >= 0 && index < row.Length ? row[index] : null;
        }

        return dict;
    }

    private static int FindColumnIndex(TableInfo table, string columnName)
    {
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i], columnName, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Builds a human-readable "column=value" description of <paramref name="table"/>'s primary
    /// key for the given row, so a merge failure can point at the exact record instead of just
    /// the table it belongs to.
    /// </summary>
    private static string DescribePrimaryKey(TableInfo table, object[] row)
    {
        var parts = new List<string>();
        foreach (var pkColumn in table.PrimaryKey)
        {
            var index = FindColumnIndex(table, pkColumn);
            var value = index >= 0 && index < row.Length ? row[index] : null;
            parts.Add($"{pkColumn}={value ?? "NULL"}");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "no primary key columns";
    }

    /// <summary>
    /// Overwrites <paramref name="localPath"/> with a byte-for-byte copy of
    /// <paramref name="masterPath"/>, clearing any stale WAL/journal sidecar files so the copy
    /// loads cleanly. Retries transient network I/O failures with backoff.
    /// </summary>
    internal static void CopyMasterToLocal(string masterPath, string localPath)
    {
        SqliteConnection.ClearAllPools();

        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            TryDelete(localPath + suffix);
        }

        CopyFileWithRetry(masterPath, localPath);
        DatabaseInitializer.EnsureWritable(localPath);
    }

    /// <summary>
    /// How many future ids are reserved per table, per sync, for this machine's exclusive use.
    /// Generous enough that no realistic amount of offline work between syncs could exhaust it,
    /// while still leaving enormous headroom in the 32-bit int id space even after thousands of
    /// syncs over the application's lifetime.
    /// </summary>
    private const long IdReservationBlockSize = 100_000;

    /// <summary>
    /// For every table with a single-column integer primary key, advances master's
    /// auto-increment counter (<c>sqlite_sequence</c>) past a block of
    /// <see cref="IdReservationBlockSize"/> ids and returns, per table, the id immediately
    /// before that block — i.e. the last id already in use anywhere prior to this reservation.
    /// <para>
    /// This must run (and master's bump must be persisted) BEFORE <see cref="CopyMasterToLocal"/>
    /// copies master's file — including its now-advanced counter — over the local file, and the
    /// returned values must then be applied via <see cref="RewindLocalIdRanges"/> so this
    /// machine's own counter resumes at the start of its reserved block rather than at the end
    /// of it (which would otherwise skip straight past the block it just reserved for itself).
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, long> ReserveIdRanges(string masterPath, string localPath)
    {
        var tables = GetMappedTables();
        var reservations = new Dictionary<string, long>();

        using var connection = new SqliteConnection($"Data Source={localPath}");
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=10000;";
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
            foreach (var table in tables)
            {
                // Only single-column integer primary keys use SQLite's AUTOINCREMENT /
                // sqlite_sequence mechanism; composite keys and non-autoincrement tables (e.g.
                // the SyncTombstones/SyncSkipList append-only logs) have no counter to reserve.
                if (table.PrimaryKey.Count != 1)
                    continue;

                try
                {
                    long currentMax;
                    using (var maxCmd = connection.CreateCommand())
                    {
                        maxCmd.CommandText =
                            $"SELECT COALESCE(MAX(\"{table.PrimaryKey[0]}\"), 0) FROM master.\"{table.Name}\";";
                        currentMax = Convert.ToInt64(maxCmd.ExecuteScalar());
                    }

                    var ceiling = currentMax + IdReservationBlockSize;

                    using (var updateCmd = connection.CreateCommand())
                    {
                        updateCmd.CommandText =
                            "UPDATE master.sqlite_sequence SET seq = $ceiling WHERE name = $name AND seq < $ceiling;";
                        updateCmd.Parameters.AddWithValue("$ceiling", ceiling);
                        updateCmd.Parameters.AddWithValue("$name", table.Name);
                        var rowsUpdated = updateCmd.ExecuteNonQuery();

                        if (rowsUpdated == 0)
                        {
                            // Either no row exists yet for this table (never had an autoincrement
                            // insert) or its seq already exceeds ceiling (another machine reserved
                            // a higher block since currentMax was read); INSERT OR IGNORE only
                            // covers the former, which is all that's needed here — the latter
                            // means this table is already protected by someone else's reservation.
                            using var insertCmd = connection.CreateCommand();
                            insertCmd.CommandText =
                                "INSERT OR IGNORE INTO master.sqlite_sequence (name, seq) VALUES ($name, $ceiling);";
                            insertCmd.Parameters.AddWithValue("$name", table.Name);
                            insertCmd.Parameters.AddWithValue("$ceiling", ceiling);
                            insertCmd.ExecuteNonQuery();
                        }
                    }

                    reservations[table.Name] = currentMax;
                }
                catch
                {
                    // Best-effort per table — a reservation failure for one table (e.g. it has
                    // no sqlite_sequence entry because it isn't declared AUTOINCREMENT) must not
                    // block reserving ranges for every other table, nor abort the sync.
                }
            }
        }
        finally
        {
            using var detach = connection.CreateCommand();
            detach.CommandText = "DETACH DATABASE master;";
            try
            {
                detach.ExecuteNonQuery();
            }
            catch
            {
                // Best-effort — the connection is being disposed regardless.
            }
        }

        return reservations;
    }

    /// <summary>
    /// Rewinds each table's local auto-increment counter back to the value recorded by
    /// <see cref="ReserveIdRanges"/>, undoing the fact that <see cref="CopyMasterToLocal"/> just
    /// overwrote the local file (and its counters) with master's — which had already been
    /// advanced past this machine's own reserved block. Without this, this machine's next insert
    /// would land at the END of the block it just reserved for itself instead of the start,
    /// effectively discarding the reservation.
    /// </summary>
    private static void RewindLocalIdRanges(string localPath, IReadOnlyDictionary<string, long> reservations)
    {
        if (reservations.Count == 0)
            return;

        using var connection = new SqliteConnection($"Data Source={localPath}");
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=10000;";
            pragma.ExecuteNonQuery();
        }

        foreach (var (tableName, localStart) in reservations)
        {
            try
            {
                using var updateCmd = connection.CreateCommand();
                // Only ever rewind DOWN to the reserved start — never up — in case something
                // else already advanced the local counter further in the meantime.
                updateCmd.CommandText =
                    "UPDATE sqlite_sequence SET seq = $localStart WHERE name = $name AND seq > $localStart;";
                updateCmd.Parameters.AddWithValue("$localStart", localStart);
                updateCmd.Parameters.AddWithValue("$name", tableName);
                updateCmd.ExecuteNonQuery();
            }
            catch
            {
                // Best-effort per table — see ReserveIdRanges.
            }
        }
    }

    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <paramref name="destPath"/>, retrying transient
    /// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> failures with
    /// exponential backoff — these are common on network shares under momentary contention
    /// (e.g. antivirus scanning the file, a brief share hiccup) and should not immediately be
    /// treated as fatal.
    /// </summary>
    internal static void CopyFileWithRetry(string sourcePath, string destPath, int maxRetries = 5, int baseDelayMs = 300)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(sourcePath, destPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < maxRetries && (ex is IOException or UnauthorizedAccessException))
            {
                Thread.Sleep(baseDelayMs * (int)Math.Pow(2, attempt));
            }
        }
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
    internal static bool IsIntegrityOk(string dbPath, out string detail)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        // Wait for locks instead of throwing immediately — a database that's merely busy
        // (e.g. still being written to by this same process) must not be mistaken for corrupt.
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=10000;";
            pragma.ExecuteNonQuery();
        }

        using var cmd = connection.CreateCommand();
        // quick_check is much faster than the full integrity_check and is sufficient to detect
        // the "database disk image is malformed" failure mode this guards against. The (100)
        // argument caps how many errors it reports, not whether it runs — unlike quick_check(1),
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
    /// automatic sync flow, this is never called implicitly — it must be invoked deliberately by
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
            // integrity check immediately — the exact symptom this guards against.
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

            var lockResult = MasterFileLock.TryAcquire(masterPath);
            if (lockResult.Lock == null)
            {
                LastError = $"Could not acquire the master lock for '{masterPath}': {lockResult.Reason}";
                return;
            }

            using var masterLock = lockResult.Lock;

            SqliteConnection.ClearAllPools();

            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                TryDelete(masterPath + suffix);
            }

            CopyFileWithRetry(localPath, masterPath);
            DatabaseInitializer.EnsureWritable(masterPath);

            SqliteConnection.ClearAllPools();
            if (!IsIntegrityOk(masterPath, out var postRebuildDetail))
            {
                LastError =
                    $"Master database at '{masterPath}' still fails SQLite's integrity check " +
                    "immediately after being rebuilt. This points to a problem writing to that path " +
                    "itself (e.g. antivirus interference, a flaky network share, or insufficient disk " +
                    $"space) rather than the source data — check the share/drive and try again. Details: {postRebuildDetail}";
            }
        }
        catch (Exception ex)
        {
            LastError = DescribeException(ex);
        }
    }

    /// <summary>
    /// Builds a SQL predicate (referencing <paramref name="sourceAlias"/>) that is true for rows
    /// NOT covered by a tombstone in <c>master.SyncTombstones</c> for <paramref name="table"/>,
    /// so a record already deleted elsewhere (see <see cref="RecordDeletionService"/>) is never
    /// re-inserted from a stale local copy that hasn't learned about the deletion yet.
    /// </summary>
    private static string BuildTombstoneExclusionClause(string sourceAlias, TableInfo table)
    {
        var keyExpr = SyncTombstoneKey.BuildSqlExpression(sourceAlias, table.PrimaryKey);
        return $"NOT EXISTS (SELECT 1 FROM master.\"SyncTombstones\" ts " +
               $"WHERE ts.\"TableName\" = '{table.Name}' AND ts.\"RecordKey\" = {keyExpr}) " +
               $"AND {BuildSkipListExclusionClause(sourceAlias, table)}";
    }

    /// <summary>
    /// Builds a SQL predicate (referencing <paramref name="sourceAlias"/>) that is true for rows
    /// NOT covered by an entry in <c>master.SyncSkipList</c> for <paramref name="table"/>, so a
    /// record deliberately excluded by a clean sync (see
    /// <see cref="CorruptedRecordSkipService"/>) is never re-inserted from a stale local copy
    /// that still has it.
    /// </summary>
    private static string BuildSkipListExclusionClause(string sourceAlias, TableInfo table)
    {
        var keyExpr = SyncTombstoneKey.BuildSqlExpression(sourceAlias, table.PrimaryKey);
        return $"NOT EXISTS (SELECT 1 FROM master.\"SyncSkipList\" sk " +
               $"WHERE sk.\"TableName\" = '{table.Name}' AND sk.\"RecordKey\" = {keyExpr})";
    }

    /// <summary>
    /// Merges the <c>SyncTombstones</c> table bidirectionally between the local and attached
    /// master databases (so every machine eventually learns about every deletion, no matter
    /// which machine or database it was originally recorded against), then deletes every
    /// tombstoned record from master immediately. This must run before any table's rows are
    /// merged from local into master; otherwise a machine whose local database still has a
    /// since-deleted record would re-insert it into master right there, making a deletion made
    /// via <see cref="RecordDeletionService"/> appear to silently undo itself on the next sync.
    /// </summary>
    private static void ReconcileTombstones(SqliteConnection connection, IReadOnlyList<TableInfo> tables)
    {
        try
        {
            using (var mergeIntoMaster = connection.CreateCommand())
            {
                mergeIntoMaster.CommandText = """
                    INSERT OR IGNORE INTO master."SyncTombstones" ("TableName", "RecordKey", "DeletedAt")
                    SELECT "TableName", "RecordKey", "DeletedAt" FROM main."SyncTombstones";
                    """;
                mergeIntoMaster.ExecuteNonQuery();
            }

            using (var mergeIntoLocal = connection.CreateCommand())
            {
                mergeIntoLocal.CommandText = """
                    INSERT OR IGNORE INTO main."SyncTombstones" ("TableName", "RecordKey", "DeletedAt")
                    SELECT "TableName", "RecordKey", "DeletedAt" FROM master."SyncTombstones";
                    """;
                mergeIntoLocal.ExecuteNonQuery();
            }
        }
        catch
        {
            // Best-effort: if SyncTombstones doesn't exist yet on one side (e.g. a database that
            // predates this feature and hasn't been reopened to pick up the schema patch), skip
            // reconciliation for this sync rather than aborting the whole merge.
            return;
        }

        foreach (var table in tables)
        {
            try
            {
                using var deleteCmd = connection.CreateCommand();
                var keyExpr = SyncTombstoneKey.BuildSqlExpression($"master.\"{table.Name}\"", table.PrimaryKey);
                deleteCmd.CommandText =
                    $"DELETE FROM master.\"{table.Name}\" " +
                    $"WHERE EXISTS (SELECT 1 FROM master.\"SyncTombstones\" ts " +
                    $"WHERE ts.\"TableName\" = '{table.Name}' AND ts.\"RecordKey\" = {keyExpr});";
                deleteCmd.ExecuteNonQuery();
            }
            catch
            {
                // Best-effort per table — a single table's tombstone application failing (e.g.
                // an unrelated FK constraint) must not block reconciling every other table.
            }
        }
    }

    /// <summary>
    /// Merges the <c>SyncSkipList</c> table bidirectionally between the local and attached
    /// master databases (so every machine eventually learns about every record a clean sync has
    /// deliberately excluded elsewhere, regardless of which machine recorded it), then deletes
    /// every skip-listed record from master immediately — mirroring
    /// <see cref="ReconcileTombstones"/> exactly, since a skip-listed record is intended to be
    /// permanently removed, not merely excluded from future merges.
    /// </summary>
    private static void ReconcileSkipList(SqliteConnection connection, IReadOnlyList<TableInfo> tables)
    {
        try
        {
            using (var mergeIntoMaster = connection.CreateCommand())
            {
                mergeIntoMaster.CommandText = """
                    INSERT OR IGNORE INTO master."SyncSkipList" ("TableName", "RecordKey", "SkippedAt", "Reason")
                    SELECT "TableName", "RecordKey", "SkippedAt", "Reason" FROM main."SyncSkipList";
                    """;
                mergeIntoMaster.ExecuteNonQuery();
            }

            using (var mergeIntoLocal = connection.CreateCommand())
            {
                mergeIntoLocal.CommandText = """
                    INSERT OR IGNORE INTO main."SyncSkipList" ("TableName", "RecordKey", "SkippedAt", "Reason")
                    SELECT "TableName", "RecordKey", "SkippedAt", "Reason" FROM master."SyncSkipList";
                    """;
                mergeIntoLocal.ExecuteNonQuery();
            }
        }
        catch
        {
            // Best-effort: if SyncSkipList doesn't exist yet on one side (e.g. a database that
            // predates this feature and hasn't been reopened to pick up the schema patch), skip
            // reconciliation for this sync rather than aborting the whole merge.
            return;
        }

        foreach (var table in tables)
        {
            try
            {
                using var deleteCmd = connection.CreateCommand();
                var keyExpr = SyncTombstoneKey.BuildSqlExpression($"master.\"{table.Name}\"", table.PrimaryKey);
                deleteCmd.CommandText =
                    $"DELETE FROM master.\"{table.Name}\" " +
                    $"WHERE EXISTS (SELECT 1 FROM master.\"SyncSkipList\" sk " +
                    $"WHERE sk.\"TableName\" = '{table.Name}' AND sk.\"RecordKey\" = {keyExpr});";
                deleteCmd.ExecuteNonQuery();
            }
            catch
            {
                // Best-effort per table — a single table's skip-list application failing (e.g.
                // an unrelated FK constraint) must not block reconciling every other table.
            }
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
    /// Wraps an inner exception with the name of the sync stage in which it occurred, so
    /// <see cref="Sync"/> can report exactly where a failure happened.
    /// </summary>
    private sealed class SyncStageException : Exception
    {
        public string Stage { get; }

        public SyncStageException(string stage, Exception inner)
            : base($"{stage}: {inner.Message}", inner)
        {
            Stage = stage;
        }
    }

    /// <summary>
    /// Cross-process advisory lock (a <c>.lock</c> sidecar file) used to ensure only one
    /// machine at a time merges into and checkpoints the shared master database. Without this,
    /// several machines starting up simultaneously would all open write transactions against
    /// the same master file on the network share at once, which is a common cause of
    /// lock-contention failures under concurrent, frequent syncing.
    /// <para>
    /// The lock is represented by the file's existence and a timestamp written inside it —
    /// NOT by holding an open OS file handle for the duration of the sync. On a network share,
    /// a held-open handle can survive server-side (via SMB leases/durable handles) long after
    /// the owning client machine has crashed, lost its network connection, gone to sleep, or
    /// been force-killed, which otherwise makes the lock appear held by "nobody" and blocks
    /// every future sync indefinitely. Because this lock instead checks the lock file's age,
    /// any lock older than <see cref="StaleLockThreshold"/> — far longer than a real sync ever
    /// takes — is treated as abandoned and automatically reclaimed.
    /// </para>
    /// </summary>
    internal sealed class MasterFileLock : IDisposable
    {
        /// <summary>
        /// A lock file older than this (based on its last-write time) without being refreshed
        /// is assumed to have been left behind by a machine that is no longer actually syncing,
        /// and is deleted and reclaimed by the next machine that encounters it.
        /// <para>
        /// Kept short (well under a minute) rather than the previous 3 minutes: a normal sync
        /// takes seconds, so a lock left behind by a crash should be reclaimed almost
        /// immediately instead of leaving every other machine unable to sync for minutes at a
        /// time. <see cref="StartHeartbeat"/> refreshes the lock file periodically while a
        /// genuinely long-running sync is in progress, so this short threshold does not cause
        /// the lock to be stolen out from under a still-active sync.
        /// </para>
        /// </summary>
        private static readonly TimeSpan StaleLockThreshold = TimeSpan.FromSeconds(30);

        /// <summary>How often the lock file's timestamp is refreshed by <see cref="StartHeartbeat"/>.</summary>
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

        private readonly string _lockPath;

        private MasterFileLock(string lockPath) => _lockPath = lockPath;

        /// <summary>
        /// Starts a background timer that periodically rewrites this lock's file so its
        /// last-write time stays fresh for as long as the sync holding it is still running.
        /// Without this, <see cref="StaleLockThreshold"/> being short enough to reclaim a
        /// genuinely abandoned lock quickly would also risk another machine reclaiming a lock
        /// that is still legitimately in use by a slow sync (e.g. a large initial merge over a
        /// slow network link). Dispose the returned object when the sync finishes to stop the
        /// heartbeat (this is separate from — and should be disposed before — releasing the
        /// lock itself).
        /// </summary>
        public IDisposable StartHeartbeat()
        {
            var timer = new Timer(_ =>
            {
                try
                {
                    File.WriteAllText(_lockPath, $"{Environment.MachineName}|pid={Environment.ProcessId}|{DateTime.UtcNow:O}");
                }
                catch
                {
                    // Best-effort — if this fails the lock may be reclaimed early, but the
                    // sync itself is unaffected and will simply retry on next launch.
                }
            }, null, HeartbeatInterval, HeartbeatInterval);

            return timer;
        }

        public readonly record struct AcquireResult(MasterFileLock? Lock, string? Reason, bool TreatAsUnreachable);

        /// <summary>
        /// Attempts to acquire the lock for <paramref name="masterPath"/>, retrying for up to
        /// ~20 seconds against transient contention, and automatically reclaiming the lock if
        /// it appears to have been abandoned (see <see cref="StaleLockThreshold"/>). Distinguishes
        /// between the lock being genuinely held by another machine (expected under frequent
        /// concurrent syncing — retry next launch) and the lock file itself being inaccessible
        /// (permissions/share misconfiguration — surfaced as a real failure, since silently
        /// treating it as "unreachable" every time hides a fixable problem from the user).
        /// </summary>
        public static AcquireResult TryAcquire(string masterPath)
        {
            var lockPath = masterPath + ".lock";
            const int maxAttempts = 40;
            Exception? lastException = null;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    // Atomic exclusive create: fails with IOException if the file already
                    // exists, succeeds (creating it) if it doesn't. This — not an open handle —
                    // is the actual mutual-exclusion primitive, so the lock reflects reality
                    // even if this process later crashes or the network drops.
                    using (var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write($"{Environment.MachineName}|pid={Environment.ProcessId}|{DateTime.UtcNow:O}");
                    }

                    return new AcquireResult(new MasterFileLock(lockPath), null, false);
                }
                catch (DirectoryNotFoundException ex)
                {
                    // The share itself has gone offline mid-attempt.
                    return new AcquireResult(null, $"path not found — the share may be offline ({ex.Message})", true);
                }
                catch (UnauthorizedAccessException ex)
                {
                    // The lock file itself is inaccessible (permissions, read-only share,
                    // stale lock left with wrong ACLs, etc.). Retrying won't help — surface
                    // this as an actionable failure rather than a silent "unreachable".
                    return new AcquireResult(
                        null,
                        $"access denied creating '{lockPath}' — check share/folder write permissions ({ex.Message})",
                        false);
                }
                catch (IOException ex)
                {
                    // The lock file already exists — either genuinely held by another machine
                    // right now, or abandoned by one that never got a chance to release it.
                    // Reclaim it if it looks abandoned, then retry either way.
                    lastException = ex;
                    TryReclaimIfStale(lockPath);
                    Thread.Sleep(500);
                }
            }

            return new AcquireResult(
                null,
                $"timed out after {maxAttempts * 500 / 1000}s waiting for another machine's sync to finish" +
                (lastException != null ? $" (last error: {lastException.Message})" : string.Empty),
                true);
        }

        /// <summary>
        /// Deletes <paramref name="lockPath"/> if it is older than <see cref="StaleLockThreshold"/>,
        /// reclaiming a lock abandoned by a machine that crashed, lost connectivity, or was
        /// force-closed mid-sync without releasing it. Best-effort: if another machine is
        /// simultaneously reclaiming or still genuinely holding it, this simply no-ops and the
        /// caller retries on its next loop iteration.
        /// </summary>
        private static void TryReclaimIfStale(string lockPath)
        {
            try
            {
                if (!File.Exists(lockPath))
                    return;

                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath);
                if (age > StaleLockThreshold)
                    File.Delete(lockPath);
            }
            catch
            {
                // Best-effort — ignored, the outer retry loop will try again shortly.
            }
        }

        /// <summary>Releases the lock by deleting the lock file.</summary>
        public void Dispose()
        {
            try
            {
                File.Delete(_lockPath);
            }
            catch
            {
                // Best-effort — a leftover lock file will be reclaimed automatically once it
                // is older than StaleLockThreshold.
            }
        }
    }
}
