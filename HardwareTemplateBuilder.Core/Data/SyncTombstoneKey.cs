using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Data;

/// <summary>
/// Builds a stable, order-independent string identifying a record by its primary key column
/// values, used by the <c>SyncTombstones</c> table (see <see cref="RecordDeletionService"/> and
/// <see cref="MasterSyncService"/>) to remember that a record was deleted so a later sync does
/// not silently resurrect it.
/// <para>
/// The same format can be computed both in C# (<see cref="Build"/>, when recording or checking a
/// tombstone for a specific record) and in plain SQL (<see cref="BuildSqlExpression"/>, when
/// filtering an entire table's rows against the tombstone table during a merge), so the two never
/// disagree on what a given row's key looks like.
/// </para>
/// </summary>
internal static class SyncTombstoneKey
{
    // Deliberately NOT a NUL character: this marker is interpolated directly into raw SQL text
    // (not bound as a parameter) by BuildSqlExpression, and a NUL embedded in a string passed to
    // SQLite's native API truncates it there, silently corrupting the generated SQL (it ends up
    // as an unterminated string literal, which SQLite reports as "unrecognized token: \"'\"").
    private const string NullMarker = "\u0001__SYNC_TOMBSTONE_NULL__\u0001";

    /// <summary>Builds the key string for a record given its primary key column values.</summary>
    public static string Build(IReadOnlyList<string> primaryKeyColumns, IReadOnlyDictionary<string, object?> keyValues)
    {
        var parts = primaryKeyColumns.Select(column =>
            keyValues.TryGetValue(column, out var value) && value != null
                ? System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? NullMarker
                : NullMarker);
        return string.Join("|", parts);
    }

    /// <summary>
    /// Builds a SQL expression (referencing <paramref name="tableAlias"/>) that computes the same
    /// key string as <see cref="Build"/> for each row of a table, for use in a
    /// <c>WHERE key_expr IN (...)</c> / <c>NOT EXISTS</c> clause against <c>SyncTombstones.RecordKey</c>.
    /// </summary>
    public static string BuildSqlExpression(string tableAlias, IReadOnlyList<string> primaryKeyColumns)
    {
        var parts = primaryKeyColumns
            .Select(column => $"COALESCE(CAST({tableAlias}.\"{column}\" AS TEXT), '{NullMarker}')");
        return string.Join(" || '|' || ", parts);
    }
}
