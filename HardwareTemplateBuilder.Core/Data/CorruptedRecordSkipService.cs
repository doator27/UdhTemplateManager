using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using HardwareTemplateBuilder.Core.Services;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Implements the "Clean &amp; Sync" workflow: given a set of records that repeatedly fail to
/// merge (typically <see cref="MasterSyncService.LastFailedRecords"/>), builds a clean copy of
/// the shared master database with those records — and any of their children that are not
/// referenced by any other surviving record — permanently removed, records every removed record
/// in the shared <c>SyncSkipList</c> table so no machine ever re-imports it again, merges this
/// machine's local changes into that clean copy, and promotes the result as the new master and
/// new local database. A full backup of the original master is taken first (see
/// <see cref="DatabaseBackupService"/>), before anything is modified.
/// </summary>
public static class CorruptedRecordSkipService
{
    /// <summary>A single record (and the reason) to permanently exclude from future syncs.</summary>
    public sealed record SkipCandidate(string TableName, IReadOnlyDictionary<string, object?> PrimaryKey, string Reason);

    /// <summary>Result of a <see cref="CreateCleanMasterAndSync"/> attempt.</summary>
    public sealed record CleanSyncResult(
        bool Success,
        string Message,
        string? BackupPath,
        IReadOnlyList<string> SkippedRecordDescriptions);

    private sealed record DeletionEntry(string TableName, IReadOnlyDictionary<string, object?> PrimaryKey, string Reason);

    /// <summary>
    /// Runs the full clean-copy workflow described in the class summary. Never throws — every
    /// failure is reported via <see cref="CleanSyncResult.Success"/>/<see cref="CleanSyncResult.Message"/>
    /// and, whenever possible, leaves the original master and local databases untouched.
    /// </summary>
    /// <param name="masterPath">Configured master database path.</param>
    /// <param name="localPath">This machine's local, per-machine working database path.</param>
    /// <param name="backupDirectory">The standard backup directory (see <see cref="DatabaseBackupService"/>).</param>
    /// <param name="recordsToSkip">
    /// The corrupted records to permanently exclude, typically built from
    /// <see cref="MasterSyncService.LastFailedRecords"/>.
    /// </param>
    public static CleanSyncResult CreateCleanMasterAndSync(
        string masterPath,
        string localPath,
        string backupDirectory,
        IReadOnlyList<SkipCandidate> recordsToSkip)
    {
        if (string.IsNullOrWhiteSpace(masterPath) || !File.Exists(masterPath))
            return new CleanSyncResult(false, $"Master database not found at '{masterPath}'.", null, Array.Empty<string>());

        if (recordsToSkip == null || recordsToSkip.Count == 0)
            return new CleanSyncResult(false, "No records were specified to skip.", null, Array.Empty<string>());

        string? backupPath = null;
        string? tempCleanPath = null;

        try
        {
            var backupResult = new DatabaseBackupService().CreateBackup(masterPath, backupDirectory);
            if (!backupResult.Success)
                return new CleanSyncResult(
                    false, $"Backup failed, aborting clean sync: {backupResult.ErrorMessage}", null, Array.Empty<string>());

            backupPath = backupResult.BackupPath;

            var lockResult = MasterSyncService.MasterFileLock.TryAcquire(masterPath);
            if (lockResult.Lock == null)
                return new CleanSyncResult(
                    false, $"Could not acquire the master sync lock: {lockResult.Reason}", backupPath, Array.Empty<string>());

            using var masterLock = lockResult.Lock;
            using var heartbeat = masterLock.StartHeartbeat();

            DatabaseInitializer.ClearJournalSidecars(masterPath);
            DatabaseInitializer.InitializeAtPath(masterPath);
            DatabaseInitializer.EnsureWritable(masterPath);

            tempCleanPath = masterPath + ".cleansync.tmp";
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
                TryDelete(tempCleanPath + suffix);

            var schema = RecordDeletionService.GetSchemaTables();
            var skippedDescriptions = new List<string>();
            var salvageWarnings = new List<string>();

            // Compute which rows to exclude (the requested records plus their exclusive
            // children) by querying the ORIGINAL master directly. This must happen before the
            // export below, and is resilient to the same physical corruption the export handles
            // — an unreadable table/row here is simply skipped rather than aborting.
            Dictionary<string, DeletionEntry> toDelete;
            using (var sourceConnection = new SqliteConnection($"Data Source={masterPath}"))
            {
                sourceConnection.Open();
                using (var pragma = sourceConnection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA busy_timeout=10000;";
                    pragma.ExecuteNonQuery();
                }

                toDelete = DetermineDeletionSet(sourceConnection, schema, recordsToSkip);
            }

            // Build tempCleanPath as a brand-new, physically valid database and copy every
            // surviving row into it table by table (excluding rows in toDelete), falling back to
            // a resilient row-by-row salvage read when a table can't be bulk-copied because of
            // corruption. This both prunes the skip candidates AND repairs any unrelated
            // physical corruption in the same pass — a byte-for-byte copy of the master (the
            // previous approach) would preserve any physical-level corruption (damaged b-tree
            // pages) the master may already have, which DELETE statements cannot repair:
            // SQLite's quick_check would keep failing afterward even once every requested
            // record is removed, regardless of whether the corrupted pages belong to those
            // records at all.
            ExportCleanCopy(masterPath, tempCleanPath, schema, toDelete, salvageWarnings);
            DatabaseInitializer.EnsureWritable(tempCleanPath);

            using (var connection = new SqliteConnection($"Data Source={tempCleanPath}"))
            {
                connection.Open();
                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA busy_timeout=10000;";
                    pragma.ExecuteNonQuery();
                }

                foreach (var entry in toDelete.Values)
                {
                    using var transaction = connection.BeginTransaction();
                    try
                    {
                        RecordSkip(connection, transaction, schema, entry.TableName, entry.PrimaryKey, entry.Reason);
                        transaction.Commit();
                        skippedDescriptions.Add($"{entry.TableName} ({DescribeKey(entry.PrimaryKey)})");
                    }
                    catch
                    {
                        try { transaction.Rollback(); } catch { /* best-effort */ }
                    }
                }
            }

            SqliteConnection.ClearAllPools();

            if (!MasterSyncService.IsIntegrityOk(tempCleanPath, out var pruneIntegrityDetail))
            {
                TryDelete(tempCleanPath);
                return new CleanSyncResult(
                    false,
                    $"The clean copy still failed SQLite's integrity check after being rebuilt from scratch: {pruneIntegrityDetail}",
                    backupPath,
                    skippedDescriptions);
            }

            var failedRecords = new List<MasterSyncService.FailedRecord>();
            try
            {
                MasterSyncService.MergeLocalIntoMaster(tempCleanPath, localPath, failedRecords);
            }
            catch (InvalidOperationException)
            {
                // MergeLocalIntoMaster throws only to report that one or more OTHER (unrelated)
                // records also failed to merge — every table's failure is already captured in
                // failedRecords, and every table that succeeded already committed its own rows.
                // This is expected/recoverable here: the clean-sync operation's job is to remove
                // the records the caller identified, not to guarantee every other record merges
                // too, so it must still proceed to promote the clean copy rather than aborting
                // and discarding the pruning work done above.
            }

            SqliteConnection.ClearAllPools();

            if (!MasterSyncService.IsIntegrityOk(tempCleanPath, out var postMergeIntegrityDetail))
            {
                TryDelete(tempCleanPath);
                return new CleanSyncResult(
                    false,
                    $"The clean copy failed SQLite's integrity check after merging local changes: {postMergeIntegrityDetail}",
                    backupPath,
                    skippedDescriptions);
            }

            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                TryDelete(masterPath + suffix);

            MasterSyncService.CopyFileWithRetry(tempCleanPath, masterPath);
            DatabaseInitializer.EnsureWritable(masterPath);

            MasterSyncService.CopyMasterToLocal(masterPath, localPath);

            TryDelete(tempCleanPath);

            var mergeNote = failedRecords.Count > 0
                ? $" {failedRecords.Count} other record(s) still failed to merge after cleaning; see MasterSyncService.LastFailedRecords."
                : string.Empty;
            var salvageNote = salvageWarnings.Count > 0
                ? $" {salvageWarnings.Count} table(s) required row-by-row salvage due to pre-existing physical corruption in the original master; unreadable rows there were skipped and are now gone."
                : string.Empty;

            return new CleanSyncResult(
                true,
                $"Backed up the original master to '{backupPath}', rebuilt it from scratch (repairing any physical corruption), permanently removed " +
                $"{skippedDescriptions.Count} record(s) (the requested records plus any exclusive children), " +
                $"merged this machine's local changes into the clean copy, and promoted it as the new master " +
                $"and new local database.{salvageNote}{mergeNote}",
                backupPath,
                skippedDescriptions);
        }
        catch (Exception ex)
        {
            if (tempCleanPath != null)
                TryDelete(tempCleanPath);

            return new CleanSyncResult(false, $"Clean sync failed: {ex.Message}", backupPath, Array.Empty<string>());
        }
    }

    /// <summary>
    /// Starting from <paramref name="roots"/>, walks the FK graph to find every "exclusive"
    /// child — a row whose only references come from records already slated for removal — and
    /// includes it in the returned deletion set. A child still referenced by a surviving record
    /// (one not being removed) is left alone entirely, along with its own children.
    /// </summary>
    private static Dictionary<string, DeletionEntry> DetermineDeletionSet(
        SqliteConnection connection,
        IReadOnlyList<RecordDeletionService.TableSchemaInfo> schema,
        IReadOnlyList<SkipCandidate> roots)
    {
        var result = new Dictionary<string, DeletionEntry>();
        var queue = new Queue<DeletionEntry>();

        foreach (var root in roots)
        {
            var key = MakeKey(root.TableName, root.PrimaryKey);
            if (!result.ContainsKey(key))
            {
                var entry = new DeletionEntry(root.TableName, root.PrimaryKey, root.Reason);
                result.Add(key, entry);
                queue.Enqueue(entry);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var child in schema)
            {
                foreach (var fk in child.ForeignKeys)
                {
                    if (!string.Equals(fk.PrincipalTable, current.TableName, StringComparison.Ordinal))
                        continue;

                    foreach (var childKey in FindReferencingRows(connection, child, fk, current.PrimaryKey))
                    {
                        var childKeyStr = MakeKey(child.Name, childKey);
                        if (result.ContainsKey(childKeyStr))
                            continue;

                        if (IsReferencedBySurvivor(connection, schema, child.Name, childKey, result))
                            continue; // still used elsewhere — keep it and its own children

                        var entry = new DeletionEntry(
                            child.Name,
                            childKey,
                            $"Exclusive child of skipped record '{current.TableName}' ({DescribeKey(current.PrimaryKey)})");
                        result.Add(childKeyStr, entry);
                        queue.Enqueue(entry);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Returns <c>true</c> if any row NOT already in <paramref name="toDelete"/> references
    /// <paramref name="childKeyValues"/> in <paramref name="childTableName"/> via a foreign key —
    /// i.e. the record is still "used by other records" and must not be deleted.
    /// </summary>
    private static bool IsReferencedBySurvivor(
        SqliteConnection connection,
        IReadOnlyList<RecordDeletionService.TableSchemaInfo> schema,
        string childTableName,
        IReadOnlyDictionary<string, object?> childKeyValues,
        Dictionary<string, DeletionEntry> toDelete)
    {
        foreach (var referencer in schema)
        {
            foreach (var fk in referencer.ForeignKeys)
            {
                if (!string.Equals(fk.PrincipalTable, childTableName, StringComparison.Ordinal))
                    continue;

                foreach (var referencingRowKey in FindReferencingRows(connection, referencer, fk, childKeyValues))
                {
                    var referencingKeyStr = MakeKey(referencer.Name, referencingRowKey);
                    if (!toDelete.ContainsKey(referencingKeyStr))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Finds every row in <paramref name="childTable"/> whose columns for <paramref name="fk"/>
    /// equal <paramref name="parentKeyValues"/>, returning each matching row's own primary key.
    /// </summary>
    private static List<Dictionary<string, object?>> FindReferencingRows(
        SqliteConnection connection,
        RecordDeletionService.TableSchemaInfo childTable,
        RecordDeletionService.ForeignKeyInfo fk,
        IReadOnlyDictionary<string, object?> parentKeyValues)
    {
        var rows = new List<Dictionary<string, object?>>();
        var whereParts = new List<string>();
        var paramValues = new List<object?>();

        for (var i = 0; i < fk.Columns.Count; i++)
        {
            if (!parentKeyValues.TryGetValue(fk.PrincipalColumns[i], out var value))
                return rows;

            if (value == null)
            {
                whereParts.Add($"\"{fk.Columns[i]}\" IS NULL");
            }
            else
            {
                whereParts.Add($"\"{fk.Columns[i]}\" = $p{i}");
                paramValues.Add(value);
            }
        }

        try
        {
            using var cmd = connection.CreateCommand();
            var pkColumnList = string.Join(", ", childTable.PrimaryKey.Select(c => $"\"{c}\""));
            cmd.CommandText = $"SELECT {pkColumnList} FROM \"{childTable.Name}\" WHERE {string.Join(" AND ", whereParts)};";
            var idx = 0;
            foreach (var value in paramValues)
            {
                cmd.Parameters.AddWithValue($"$p{idx}", value);
                idx++;
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < childTable.PrimaryKey.Count; i++)
                {
                    var value = reader.GetValue(i);
                    row[childTable.PrimaryKey[i]] = value is DBNull ? null : value;
                }

                rows.Add(row);
            }
        }
        catch
        {
            // Best-effort — an unreadable child table/row must not block skipping the rest.
        }

        return rows;
    }

    /// <summary>
    /// Records the given record in <c>SyncSkipList</c>. The row itself is never deleted here —
    /// <see cref="ExportCleanCopy"/> already excludes it (and its exclusive children) while
    /// building <paramref name="connection"/>'s database from scratch, so all that remains is
    /// persisting the permanent exclusion entry.
    /// </summary>
    private static void RecordSkip(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<RecordDeletionService.TableSchemaInfo> schema,
        string tableName,
        IReadOnlyDictionary<string, object?> primaryKey,
        string reason)
    {
        var table = schema.First(t => string.Equals(t.Name, tableName, StringComparison.Ordinal));
        var recordKey = SyncTombstoneKey.Build(table.PrimaryKey, primaryKey);
        using var skipCmd = connection.CreateCommand();
        skipCmd.Transaction = transaction;
        skipCmd.CommandText =
            "INSERT OR REPLACE INTO \"SyncSkipList\" (\"TableName\", \"RecordKey\", \"SkippedAt\", \"Reason\") " +
            "VALUES ($tableName, $recordKey, $skippedAt, $reason);";
        skipCmd.Parameters.AddWithValue("$tableName", tableName);
        skipCmd.Parameters.AddWithValue("$recordKey", recordKey);
        skipCmd.Parameters.AddWithValue("$skippedAt", DateTime.UtcNow.ToString("O"));
        skipCmd.Parameters.AddWithValue("$reason", reason);
        skipCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Builds a brand-new, physically valid SQLite database at <paramref name="destPath"/>
    /// (via <see cref="DatabaseInitializer.InitializeAtPath"/>, so it has the current schema
    /// applied) and copies every row from <paramref name="sourcePath"/> into it, table by table,
    /// skipping any row whose primary key is in <paramref name="toExclude"/>.
    /// <para>
    /// For each table, a fast bulk <c>ATTACH</c> + <c>INSERT ... SELECT</c> is attempted first.
    /// If that fails — the expected outcome when <paramref name="sourcePath"/> has physical
    /// corruption (damaged b-tree pages) touching that table — falls back to reading the source
    /// table one row at a time and inserting whatever rows can still be read, recording a
    /// warning in <paramref name="salvageWarnings"/> and silently dropping only the specific
    /// rows that can't be read. This guarantees <paramref name="destPath"/> ends up physically
    /// valid (unlike copying the corrupted file directly and deleting rows from it in place,
    /// which leaves any pre-existing damaged pages untouched).
    /// </para>
    /// </summary>
    private static void ExportCleanCopy(
        string sourcePath,
        string destPath,
        IReadOnlyList<RecordDeletionService.TableSchemaInfo> schema,
        IReadOnlyDictionary<string, DeletionEntry> toExclude,
        List<string> salvageWarnings)
    {
        DatabaseInitializer.InitializeAtPath(destPath);
        SqliteConnection.ClearAllPools();

        using var destConnection = new SqliteConnection($"Data Source={destPath}");
        destConnection.Open();
        using (var pragma = destConnection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=OFF;";
            pragma.ExecuteNonQuery();
        }

        using (var attach = destConnection.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $sourcePath AS src;";
            attach.Parameters.AddWithValue("$sourcePath", sourcePath);
            attach.ExecuteNonQuery();
        }

        try
        {
            foreach (var table in schema)
            {
                var columnList = string.Join(", ", table.Columns.Select(c => $"\"{c}\""));
                var excludeClause = BuildExclusionClause(table, toExclude);

                using (var deleteExisting = destConnection.CreateCommand())
                {
                    // InitializeAtPath seeds a few tables (e.g. default AppSettings rows) that
                    // would otherwise collide with/duplicate the source's own rows for the same
                    // table during the bulk copy below.
                    deleteExisting.CommandText = $"DELETE FROM main.\"{table.Name}\";";
                    deleteExisting.ExecuteNonQuery();
                }

                try
                {
                    using var bulkTransaction = destConnection.BeginTransaction();
                    using (var bulkCmd = destConnection.CreateCommand())
                    {
                        bulkCmd.Transaction = bulkTransaction;
                        bulkCmd.CommandText =
                            $"INSERT INTO main.\"{table.Name}\" ({columnList}) " +
                            $"SELECT {columnList} FROM src.\"{table.Name}\" AS src{excludeClause};";
                        bulkCmd.ExecuteNonQuery();
                    }

                    bulkTransaction.Commit();
                }
                catch (Exception bulkEx)
                {
                    // Bulk copy failed — most likely because reading the source table hit a
                    // physically corrupted page. Fall back to salvaging whatever rows can still
                    // be read one at a time.
                    try
                    {
                        using var clearCmd = destConnection.CreateCommand();
                        clearCmd.CommandText = $"DELETE FROM main.\"{table.Name}\";";
                        clearCmd.ExecuteNonQuery();
                    }
                    catch { /* best-effort */ }

                    var salvaged = SalvageTableRowByRow(sourcePath, destConnection, table, toExclude);
                    salvageWarnings.Add(
                        $"{table.Name}: bulk copy failed ({bulkEx.Message}); salvaged {salvaged} row(s) individually.");
                }
            }
        }
        finally
        {
            using var detach = destConnection.CreateCommand();
            detach.CommandText = "DETACH DATABASE src;";
            try { detach.ExecuteNonQuery(); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Builds a SQL clause (starting with a space, appendable directly after the source table
    /// alias reference) excluding every row of <paramref name="table"/> present in
    /// <paramref name="toExclude"/>, or an empty string if none of that table's rows are
    /// excluded.
    /// </summary>
    private static string BuildExclusionClause(
        RecordDeletionService.TableSchemaInfo table,
        IReadOnlyDictionary<string, DeletionEntry> toExclude)
    {
        var keys = toExclude.Values
            .Where(e => string.Equals(e.TableName, table.Name, StringComparison.Ordinal))
            .Select(e => e.PrimaryKey)
            .ToList();

        if (keys.Count == 0)
            return string.Empty;

        var orClauses = new List<string>();
        foreach (var key in keys)
        {
            var parts = table.PrimaryKey.Select(pk =>
                key.TryGetValue(pk, out var value) && value != null
                    ? $"src.\"{pk}\" = {FormatLiteral(value)}"
                    : $"src.\"{pk}\" IS NULL");
            orClauses.Add($"({string.Join(" AND ", parts)})");
        }

        return $" WHERE NOT ({string.Join(" OR ", orClauses)})";
    }

    /// <summary>
    /// Formats a primitive value as a SQL literal for embedding directly in a generated
    /// statement. Only ever called with primary key values read back from SQLite itself
    /// (never raw user input), so this is safe despite not using parameters — the exclusion
    /// clause needs to name an arbitrary, dynamic number of keys per table.
    /// </summary>
    private static string FormatLiteral(object value) => value switch
    {
        string s => "'" + s.Replace("'", "''") + "'",
        bool b => b ? "1" : "0",
        DateTime dt => "'" + dt.ToString("O") + "'",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"
    };

    /// <summary>
    /// Reads <paramref name="table"/> from <paramref name="sourcePath"/> one row at a time,
    /// inserting each readable, non-excluded row into <paramref name="destConnection"/>.
    /// Used only as a fallback when a table can't be bulk-copied (see
    /// <see cref="ExportCleanCopy"/>) — typically because physical corruption affects some of
    /// its pages. A single unreadable row (or a read failure partway through the table) simply
    /// stops salvage for the remainder of that table rather than losing every row already
    /// successfully copied.
    /// </summary>
    private static int SalvageTableRowByRow(
        string sourcePath,
        SqliteConnection destConnection,
        RecordDeletionService.TableSchemaInfo table,
        IReadOnlyDictionary<string, DeletionEntry> toExclude)
    {
        var columnList = string.Join(", ", table.Columns.Select(c => $"\"{c}\""));
        var excludedKeys = new HashSet<string>(
            toExclude.Values
                .Where(e => string.Equals(e.TableName, table.Name, StringComparison.Ordinal))
                .Select(e => MakeKey(e.TableName, e.PrimaryKey)));

        var paramNames = table.Columns.Select((_, i) => $"$p{i}").ToList();
        var insertSql = $"INSERT INTO main.\"{table.Name}\" ({columnList}) VALUES ({string.Join(", ", paramNames)});";
        var salvaged = 0;

        using var sourceConnection = new SqliteConnection($"Data Source={sourcePath}");
        sourceConnection.Open();
        using (var pragma = sourceConnection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=10000;";
            pragma.ExecuteNonQuery();
        }

        try
        {
            using var selectCmd = sourceConnection.CreateCommand();
            selectCmd.CommandText = $"SELECT {columnList} FROM \"{table.Name}\";";
            using var reader = selectCmd.ExecuteReader();

            while (true)
            {
                bool hasRow;
                try
                {
                    hasRow = reader.Read();
                }
                catch
                {
                    // The page containing the next row (or a row's overflow data) is corrupted
                    // — stop salvaging this table here; every row read so far is preserved.
                    break;
                }

                if (!hasRow)
                    break;

                object[] values;
                try
                {
                    values = new object[table.Columns.Count];
                    reader.GetValues(values);
                }
                catch
                {
                    continue;
                }

                var key = new Dictionary<string, object?>();
                for (var i = 0; i < table.PrimaryKey.Count; i++)
                {
                    var idx = table.Columns.ToList().IndexOf(table.PrimaryKey[i]);
                    key[table.PrimaryKey[i]] = idx >= 0 && values[idx] is not DBNull ? values[idx] : null;
                }

                if (excludedKeys.Contains(MakeKey(table.Name, key)))
                    continue;

                try
                {
                    using var insertCmd = destConnection.CreateCommand();
                    insertCmd.CommandText = insertSql;
                    for (var i = 0; i < table.Columns.Count; i++)
                        insertCmd.Parameters.AddWithValue(paramNames[i], values[i] ?? DBNull.Value);
                    insertCmd.ExecuteNonQuery();
                    salvaged++;
                }
                catch
                {
                    // Best-effort — a single row failing to insert (e.g. an unexpected
                    // constraint collision) must not stop the rest of the table's salvage.
                }
            }
        }
        catch
        {
            // Best-effort — the table couldn't even be opened for row-by-row reading; whatever
            // rows were salvaged before this point (none, in this case) are kept.
        }

        return salvaged;
    }

    private static string DescribeKey(IReadOnlyDictionary<string, object?> key) =>
        string.Join(", ", key.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));

    private static string MakeKey(string tableName, IReadOnlyDictionary<string, object?> key) =>
        $"{tableName}::{SyncTombstoneKey.Build(key.Keys.ToList(), key)}";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Non-fatal — a stale temp/sidecar file left behind will be overwritten next attempt.
        }
    }
}
