using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Deletes a single database record — identified by table name and primary key, typically one
/// reported in <see cref="MasterSyncService.LastFailedRecords"/> after a sync — along with every
/// other row in the database that references it via a foreign key, from both the local working
/// database and (if configured) the shared master database.
/// <para>
/// This exists specifically to let a user get rid of a record that repeatedly fails to merge
/// (e.g. a corrupted or invalid row) without needing an external SQLite tool. It is a blunt
/// instrument — it permanently removes data — so callers should confirm with the user before
/// invoking it.
/// </para>
/// </summary>
public static class RecordDeletionService
{
    /// <summary>Result of a <see cref="DeleteRecordEverywhere"/> attempt.</summary>
    /// <param name="Success">Whether the deletion completed (at least locally).</param>
    /// <param name="Message">A human-readable summary suitable for direct display to the user.</param>
    public sealed record DeletionResult(bool Success, string Message);

    /// <summary>
    /// Deletes the record identified by <paramref name="tableName"/>/<paramref name="primaryKey"/>,
    /// and every row in any other table that references it via a foreign key (applied
    /// recursively, so grandchild references are also removed), from the local database and,
    /// if <paramref name="masterPath"/> is configured and reachable, from the master database
    /// too (under the same advisory sync lock used by <see cref="MasterSyncService"/>).
    /// </summary>
    /// <param name="tableName">The table the record belongs to.</param>
    /// <param name="primaryKey">The record's primary key column names and values.</param>
    /// <param name="masterPath">Configured master database path, or <c>null</c>/empty if none.</param>
    /// <param name="localPath">The local, per-machine working database path.</param>
    /// <param name="force">
    /// When <c>false</c> (default), a single row or table that can't be read/deleted (e.g. the
    /// error typically seen with a physically corrupted record) aborts the whole operation and
    /// leaves both databases untouched, rolling back any partial progress. When <c>true</c>,
    /// every step is best-effort: read/delete failures on individual tables or rows are recorded
    /// as warnings and skipped rather than aborting, each successful delete is committed
    /// immediately (not batched in one all-or-nothing transaction) so it survives even if a
    /// later step fails, and the target record's own delete is always attempted last regardless
    /// of whether every dependent reference could be removed. Use this when a normal (non-force)
    /// delete fails because the record or its references appear corrupted.
    /// <para>
    /// Force mode also does not assume the same primary key value identifies the record in both
    /// databases. If <paramref name="primaryKey"/> doesn't exist in a given database, that
    /// database is searched for a row whose non-key columns all match the row found in the other
    /// database, and that row's own key is deleted instead. This handles local/master rows that
    /// represent the "same" record but were assigned different IDs (e.g. divergent autoincrement
    /// values). If no unambiguous content match is found either, that database is left untouched
    /// and a warning is recorded.
    /// </para>
    /// </param>
    public static DeletionResult DeleteRecordEverywhere(
        string tableName,
        IReadOnlyDictionary<string, object?> primaryKey,
        string? masterPath,
        string localPath,
        bool force = false)
    {
        if (primaryKey.Count == 0)
            return new DeletionResult(false, $"'{tableName}' has no primary key values to delete by.");

        try
        {
            var schema = GetSchemaTables();
            var table = schema.FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.Ordinal));
            if (table == null)
                return new DeletionResult(false, $"Unknown table '{tableName}' — nothing was deleted.");

            var pkDescription = string.Join(", ", primaryKey.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));
            var forceNote = force ? " (force mode: unreadable/undeletable references were skipped)" : string.Empty;

            // In force mode, capture the full row (wherever it can still be found) so each
            // database can independently resolve the matching record even if its ID differs.
            IReadOnlyDictionary<string, object?>? referenceRow = null;
            if (force)
            {
                referenceRow = GetReferenceRowFromPath(localPath, table, primaryKey);
                if (referenceRow == null && !string.IsNullOrWhiteSpace(masterPath) && System.IO.File.Exists(masterPath))
                    referenceRow = GetReferenceRowFromPath(masterPath, table, primaryKey);
            }

            var localWarnings = new List<string>();
            var localDeleted = DeleteFromDatabase(localPath, tableName, primaryKey, schema, force, localWarnings, referenceRow);

            if (string.IsNullOrWhiteSpace(masterPath))
            {
                return new DeletionResult(
                    true,
                    $"Deleted '{tableName}' record ({pkDescription}) and {Math.Max(localDeleted - 1, 0)} dependent row(s) " +
                    $"from the local database{forceNote}. No master database is configured." +
                    DescribeWarnings(localWarnings));
            }

            if (!System.IO.File.Exists(masterPath))
            {
                return new DeletionResult(
                    true,
                    $"Deleted '{tableName}' record ({pkDescription}) from the local database{forceNote}, but the " +
                    $"master database at '{masterPath}' could not be found — nothing was changed there. It will " +
                    "be removed there too the next time this record is re-encountered, or delete it manually." +
                    DescribeWarnings(localWarnings));
            }

            var lockResult = MasterSyncService.MasterFileLock.TryAcquire(masterPath);
            if (lockResult.Lock == null)
            {
                return new DeletionResult(
                    true,
                    $"Deleted '{tableName}' record ({pkDescription}) from the local database{forceNote}, but could " +
                    $"not acquire the master sync lock to delete it there too: {lockResult.Reason}. Try again " +
                    "shortly, or delete it manually from the master." +
                    DescribeWarnings(localWarnings));
            }

            using (lockResult.Lock)
            {
                DatabaseInitializer.EnsureWritable(masterPath);
                DatabaseInitializer.ClearJournalSidecars(masterPath);
                var masterWarnings = new List<string>();
                var masterDeleted = DeleteFromDatabase(masterPath, tableName, primaryKey, schema, force, masterWarnings, referenceRow);

                return new DeletionResult(
                    true,
                    $"Deleted '{tableName}' record ({pkDescription}) and its dependent references{forceNote} " +
                    $"({localDeleted} row(s) locally, {masterDeleted} row(s) in the master database)." +
                    DescribeWarnings(localWarnings.Concat(masterWarnings).ToList()));
            }
        }
        catch (Exception ex)
        {
            var suggestion = force ? string.Empty : " Try again with Force Delete if the record appears corrupted.";
            return new DeletionResult(false, $"Delete failed: {ex.Message}.{suggestion}");
        }
    }

    private static string DescribeWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
            return string.Empty;

        return $" {warnings.Count} step(s) were skipped: " + string.Join(" | ", warnings);
    }

    /// <summary>
    /// Opens <paramref name="dbPath"/>, cascades the delete through every dependent table, and
    /// commits in a single transaction. Returns the total number of rows deleted (the target
    /// record plus every dependent row).
    /// </summary>
    private static int DeleteFromDatabase(
        string dbPath,
        string tableName,
        IReadOnlyDictionary<string, object?> primaryKey,
        IReadOnlyList<TableSchemaInfo> schema,
        bool force,
        List<string> warnings,
        IReadOnlyDictionary<string, object?>? referenceRow = null)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=OFF;";
            pragma.ExecuteNonQuery();
        }

        if (!force)
        {
            using var transaction = connection.BeginTransaction();
            var deletedCount = 0;
            try
            {
                DeleteCascade(connection, transaction, tableName, primaryKey, schema, new HashSet<string>(), ref deletedCount, force: false, warnings: warnings);
                transaction.Commit();
                return deletedCount;
            }
            catch
            {
                try
                {
                    transaction.Rollback();
                }
                catch
                {
                    // Best-effort — see identical guards in MasterSyncService.
                }

                throw;
            }
        }

        // Force mode: no single all-or-nothing transaction. Each dependent row is deleted and
        // committed immediately as it's found, and any table/row that can't be read or deleted
        // (e.g. because the underlying data is corrupted) is skipped with a warning rather than
        // aborting the whole operation. The target record itself is always attempted last.
        var table = schema.First(t => string.Equals(t.Name, tableName, StringComparison.Ordinal));
        var effectiveKey = primaryKey;

        // Only the primary key columns are read here (not the full row) so a corrupted value in
        // some other column can't make an existing row look "missing" and get skipped.
        if (!RowExistsByKey(connection, null, table, primaryKey))
        {
            // The exact ID doesn't exist in this database (local and master IDs have diverged).
            // Fall back to matching by content against the row found in the other database.
            if (referenceRow != null)
            {
                var resolved = ResolvePrimaryKeyByContentMatch(connection, table, referenceRow);
                if (resolved != null)
                {
                    var resolvedDescription = string.Join(", ", resolved.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));
                    warnings.Add(
                        $"'{tableName}' record was not found by its original ID in this database; matched " +
                        $"an equivalent row by content instead ({resolvedDescription}).");
                    effectiveKey = resolved;
                }
                else
                {
                    warnings.Add(
                        $"'{tableName}' record was not found by its original ID in this database, and no " +
                        "single equivalent row could be matched by content — nothing was deleted here.");
                    return 0;
                }
            }
            else
            {
                warnings.Add($"'{tableName}' record was not found in this database — nothing was deleted here.");
                return 0;
            }
        }

        var forceDeletedCount = 0;
        DeleteCascade(connection, null, tableName, effectiveKey, schema, new HashSet<string>(), ref forceDeletedCount, force: true, warnings: warnings);
        return forceDeletedCount;
    }

    /// <summary>
    /// Cheaply checks whether a row identified by <paramref name="primaryKey"/> exists in
    /// <paramref name="table"/>, reading only the primary key columns (never the full row), so a
    /// corrupted value in some other column can't cause the check itself to fail.
    /// </summary>
    private static bool RowExistsByKey(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        TableSchemaInfo table,
        IReadOnlyDictionary<string, object?> primaryKey)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            var whereParts = primaryKey.Keys.Select((k, i) => $"\"{k}\" = $pk{i}").ToList();
            var pkColumnList = string.Join(", ", table.PrimaryKey.Select(c => $"\"{c}\""));
            cmd.CommandText = $"SELECT {pkColumnList} FROM \"{table.Name}\" WHERE {string.Join(" AND ", whereParts)} LIMIT 1;";
            var idx = 0;
            foreach (var value in primaryKey.Values)
            {
                cmd.Parameters.AddWithValue($"$pk{idx}", value ?? DBNull.Value);
                idx++;
            }

            using var reader = cmd.ExecuteReader();
            return reader.Read();
        }
        catch
        {
            // If even a PK-only read fails, assume the row is present but severely corrupted
            // rather than treating it as absent — that would block deletion entirely.
            return true;
        }
    }

    /// <summary>
    /// Reads every column of the row identified by <paramref name="primaryKey"/> in <paramref name="table"/>,
    /// or <c>null</c> if no such row exists.
    /// </summary>
    private static Dictionary<string, object?>? GetFullRow(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        TableSchemaInfo table,
        IReadOnlyDictionary<string, object?> primaryKey)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            var whereParts = primaryKey.Keys.Select((k, i) => $"\"{k}\" = $pk{i}").ToList();
            var columnList = string.Join(", ", table.Columns.Select(c => $"\"{c}\""));
            cmd.CommandText = $"SELECT {columnList} FROM \"{table.Name}\" WHERE {string.Join(" AND ", whereParts)} LIMIT 1;";
            var idx = 0;
            foreach (var value in primaryKey.Values)
            {
                cmd.Parameters.AddWithValue($"$pk{idx}", value ?? DBNull.Value);
                idx++;
            }

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            var row = new Dictionary<string, object?>();
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var value = reader.GetValue(i);
                row[table.Columns[i]] = value is DBNull ? null : value;
            }

            return row;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens a short-lived connection to <paramref name="dbPath"/> and reads the full row for
    /// <paramref name="primaryKey"/> in <paramref name="table"/>, or <c>null</c> if it can't be
    /// opened/found. Used to capture a reference copy of a record before it's deleted, so the
    /// other database can find the equivalent row even if its ID differs.
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? GetReferenceRowFromPath(
        string dbPath,
        TableSchemaInfo table,
        IReadOnlyDictionary<string, object?> primaryKey)
    {
        try
        {
            if (!System.IO.File.Exists(dbPath))
                return null;

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            return GetFullRow(connection, null, table, primaryKey);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Searches <paramref name="table"/> for a row whose non-primary-key columns all equal the
    /// corresponding values in <paramref name="referenceRow"/>, returning that row's primary key
    /// if exactly one such row exists (an ambiguous or absent match returns <c>null</c>).
    /// </summary>
    private static Dictionary<string, object?>? ResolvePrimaryKeyByContentMatch(
        SqliteConnection connection,
        TableSchemaInfo table,
        IReadOnlyDictionary<string, object?> referenceRow)
    {
        var nonKeyColumns = table.Columns.Where(c => !table.PrimaryKey.Contains(c)).ToList();
        if (nonKeyColumns.Count == 0)
            return null;

        var whereParts = new List<string>();
        var paramValues = new List<object?>();
        foreach (var column in nonKeyColumns)
        {
            if (!referenceRow.TryGetValue(column, out var value))
                continue;

            if (value == null)
            {
                whereParts.Add($"\"{column}\" IS NULL");
            }
            else
            {
                whereParts.Add($"\"{column}\" = $m{paramValues.Count}");
                paramValues.Add(value);
            }
        }

        if (whereParts.Count == 0)
            return null;

        try
        {
            using var cmd = connection.CreateCommand();
            var pkColumnList = string.Join(", ", table.PrimaryKey.Select(c => $"\"{c}\""));
            cmd.CommandText = $"SELECT {pkColumnList} FROM \"{table.Name}\" WHERE {string.Join(" AND ", whereParts)} LIMIT 2;";
            var idx = 0;
            foreach (var value in paramValues)
            {
                cmd.Parameters.AddWithValue($"$m{idx}", value);
                idx++;
            }

            using var reader = cmd.ExecuteReader();
            Dictionary<string, object?>? match = null;
            var matchCount = 0;
            while (reader.Read())
            {
                matchCount++;
                if (matchCount > 1)
                    return null; // Ambiguous — more than one row looks the same, don't guess.

                match = new Dictionary<string, object?>();
                for (var i = 0; i < table.PrimaryKey.Count; i++)
                {
                    var value = reader.GetValue(i);
                    match[table.PrimaryKey[i]] = value is DBNull ? null : value;
                }
            }

            return matchCount == 1 ? match : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Recursively deletes every row that references <paramref name="tableName"/>/<paramref name="keyValues"/>
    /// via a foreign key, then deletes the record itself. <paramref name="visited"/> guards
    /// against infinite recursion on self-referential/cyclic schemas.
    /// </summary>
    private static void DeleteCascade(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        IReadOnlyDictionary<string, object?> keyValues,
        IReadOnlyList<TableSchemaInfo> schema,
        HashSet<string> visited,
        ref int deletedCount,
        bool force,
        List<string> warnings)
    {
        var visitKey = tableName + "|" + string.Join(",", keyValues.Select(kv => $"{kv.Key}={kv.Value}"));
        if (!visited.Add(visitKey))
            return;

        foreach (var child in schema)
        {
            foreach (var fk in child.ForeignKeys)
            {
                if (!string.Equals(fk.PrincipalTable, tableName, StringComparison.Ordinal))
                    continue;

                var whereParts = new List<string>();
                var paramValues = new List<object?>();
                var matched = true;
                for (var i = 0; i < fk.Columns.Count; i++)
                {
                    if (!keyValues.TryGetValue(fk.PrincipalColumns[i], out var value))
                    {
                        matched = false;
                        break;
                    }

                    whereParts.Add($"\"{fk.Columns[i]}\" = $fk{i}");
                    paramValues.Add(value);
                }

                if (!matched)
                    continue;

                var childRows = new List<object[]>();
                try
                {
                    using var selectCmd = connection.CreateCommand();
                    selectCmd.Transaction = transaction;
                    var pkColumnList = string.Join(", ", child.PrimaryKey.Select(c => $"\"{c}\""));
                    selectCmd.CommandText =
                        $"SELECT {pkColumnList} FROM \"{child.Name}\" WHERE {string.Join(" AND ", whereParts)};";
                    for (var i = 0; i < paramValues.Count; i++)
                        selectCmd.Parameters.AddWithValue($"$fk{i}", paramValues[i] ?? DBNull.Value);

                    using var reader = selectCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var values = new object[child.PrimaryKey.Count];
                        reader.GetValues(values);
                        childRows.Add(values);
                    }
                }
                catch (Exception ex) when (force)
                {
                    warnings.Add($"Could not read dependents in '{child.Name}': {ex.Message}");
                    continue;
                }

                foreach (var row in childRows)
                {
                    var childKey = new Dictionary<string, object?>();
                    for (var i = 0; i < child.PrimaryKey.Count; i++)
                        childKey[child.PrimaryKey[i]] = row[i];

                    if (force)
                    {
                        try
                        {
                            DeleteCascade(connection, transaction, child.Name, childKey, schema, visited, ref deletedCount, force: true, warnings: warnings);
                        }
                        catch (Exception ex)
                        {
                            var childDescription = string.Join(", ", childKey.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));
                            warnings.Add($"Could not delete '{child.Name}' ({childDescription}): {ex.Message}");
                        }
                    }
                    else
                    {
                        DeleteCascade(connection, transaction, child.Name, childKey, schema, visited, ref deletedCount, force: false, warnings: warnings);
                    }
                }
            }
        }

        try
        {
            using var deleteCmd = connection.CreateCommand();
            deleteCmd.Transaction = transaction;
            var deleteWhere = keyValues.Keys.Select((k, i) => $"\"{k}\" = $pk{i}").ToList();
            deleteCmd.CommandText = $"DELETE FROM \"{tableName}\" WHERE {string.Join(" AND ", deleteWhere)};";
            var idx = 0;
            foreach (var value in keyValues.Values)
            {
                deleteCmd.Parameters.AddWithValue($"$pk{idx}", value ?? DBNull.Value);
                idx++;
            }

            deletedCount += deleteCmd.ExecuteNonQuery();
            WriteTombstone(connection, transaction, tableName, schema, keyValues);
        }
        catch (Exception ex) when (force)
        {
            var description = string.Join(", ", keyValues.Select(kv => $"{kv.Key}={kv.Value ?? "NULL"}"));
            warnings.Add($"Could not delete '{tableName}' ({description}): {ex.Message}");
        }
    }

    /// <summary>
    /// Records that <paramref name="tableName"/>/<paramref name="keyValues"/> was deleted, in the
    /// <c>SyncTombstones</c> table of the same database the delete just happened in. Without
    /// this, <see cref="MasterSyncService"/>'s next sync has no way to know the row was
    /// intentionally removed — it would see a local copy still holding the row (or vice versa)
    /// and simply merge it right back in, undoing the deletion. Best-effort: a failure here
    /// (e.g. the table doesn't exist yet on a very old database) must not fail the delete itself.
    /// </summary>
    private static void WriteTombstone(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        IReadOnlyList<TableSchemaInfo> schema,
        IReadOnlyDictionary<string, object?> keyValues)
    {
        try
        {
            var table = schema.FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.Ordinal));
            if (table == null)
                return;

            var recordKey = SyncTombstoneKey.Build(table.PrimaryKey, keyValues);

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText =
                "INSERT OR REPLACE INTO \"SyncTombstones\" (\"TableName\", \"RecordKey\", \"DeletedAt\") " +
                "VALUES ($tableName, $recordKey, $deletedAt);";
            cmd.Parameters.AddWithValue("$tableName", tableName);
            cmd.Parameters.AddWithValue("$recordKey", recordKey);
            cmd.Parameters.AddWithValue("$deletedAt", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Best-effort — see summary above. Worst case the row could reappear on a future
            // sync from a machine that never learned about this deletion, same as before this
            // tombstone mechanism existed.
        }
    }

    internal sealed record TableSchemaInfo(
        string Name,
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> PrimaryKey,
        IReadOnlyList<ForeignKeyInfo> ForeignKeys);

    internal sealed record ForeignKeyInfo(
        IReadOnlyList<string> Columns,
        string PrincipalTable,
        IReadOnlyList<string> PrincipalColumns);

    /// <summary>
    /// Reads table, column, primary key, and foreign key metadata from the EF model so the
    /// cascade stays in sync with the schema without hardcoding table/relationship lists.
    /// Exposed internally so <see cref="CorruptedRecordSkipService"/> can reuse the same
    /// FK graph instead of duplicating it.
    /// </summary>
    internal static IReadOnlyList<TableSchemaInfo> GetSchemaTables()
    {
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options);

        var result = new List<TableSchemaInfo>();
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

            var foreignKeys = new List<ForeignKeyInfo>();
            foreach (var fk in entityType.GetForeignKeys())
            {
                var principalTable = fk.PrincipalEntityType.GetTableName();
                if (principalTable == null)
                    continue;

                var fkColumns = fk.Properties.Select(p => p.GetColumnName()!).ToList();
                var principalColumns = fk.PrincipalKey.Properties.Select(p => p.GetColumnName()!).ToList();
                foreignKeys.Add(new ForeignKeyInfo(fkColumns, principalTable, principalColumns));
            }

            result.Add(new TableSchemaInfo(tableName, allColumns, pkColumns, foreignKeys));
        }

        return result;
    }
}
