using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Describes what happened (or will happen) to a single row when merging a legacy
/// SQLite database into the master database via <see cref="DatabaseMergeImportService"/>.
/// </summary>
public enum TableMergeAction
{
    /// <summary>The row already exists in the master database (same Id, matching identity fields) — nothing imported.</summary>
    Skip,

    /// <summary>The Id was not in use in the master database, so the row is imported unchanged with the same Id.</summary>
    InsertSameId,

    /// <summary>The Id was already used by a different record in the master database, so the row is imported with a newly allocated Id (and any child references are remapped).</summary>
    InsertNewId
}

/// <summary>Describes the planned (or applied) outcome for one source row.</summary>
public sealed class MergeRowResult
{
    /// <summary>Gets the row's Id in the legacy source database.</summary>
    public int OldId { get; init; }

    /// <summary>Gets the Id the row has (or will have) in the master database.</summary>
    public int NewId { get; init; }

    /// <summary>Gets the action taken for this row.</summary>
    public TableMergeAction Action { get; init; }

    /// <summary>Gets a short human-readable description of the row (for preview display).</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>Summarizes the merge plan for a single table.</summary>
public sealed class TableMergePreview
{
    /// <summary>Gets the table/entity name.</summary>
    public string TableName { get; init; } = string.Empty;

    /// <summary>Gets the per-row results for this table.</summary>
    public List<MergeRowResult> Results { get; init; } = new();

    /// <summary>Gets the number of rows that already exist in the master database and will be skipped.</summary>
    public int SkipCount => Results.Count(r => r.Action == TableMergeAction.Skip);

    /// <summary>Gets the number of rows that will be inserted using their original Id.</summary>
    public int InsertSameIdCount => Results.Count(r => r.Action == TableMergeAction.InsertSameId);

    /// <summary>Gets the number of rows that will be inserted with a newly allocated Id.</summary>
    public int InsertNewIdCount => Results.Count(r => r.Action == TableMergeAction.InsertNewId);

    /// <summary>
    /// Gets or sets an optional note explaining why this table was not processed (e.g. the
    /// table does not yet exist in the master database because pending migrations have not
    /// been applied). Null when the table was processed normally.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>Gets a one-line summary of this table's planned changes.</summary>
    public string Summary =>
        Note != null
            ? $"{TableName}: SKIPPED — {Note}"
            : $"{TableName}: {Results.Count} row(s) — {SkipCount} already present, " +
              $"{InsertSameIdCount} new (same Id), {InsertNewIdCount} new (remapped Id)";
}

/// <summary>
/// The full plan produced by <see cref="DatabaseMergeImportService.PreviewAsync"/>, describing
/// what would be imported from a legacy SQLite database into the master database.
/// Pass the same instance back to <see cref="DatabaseMergeImportService.ApplyAsync"/> to commit it.
/// </summary>
public sealed class MergeImportPlan
{
    /// <summary>Gets the path to the legacy SQLite database file this plan was computed from.</summary>
    public string SourceFilePath { get; init; } = string.Empty;

    /// <summary>Gets the per-table previews, in the order they will be applied.</summary>
    public List<TableMergePreview> TablePreviews { get; init; } = new();

    /// <summary>Gets whether this plan contains any rows that would actually be written.</summary>
    public bool HasChanges => TablePreviews.Any(t => t.InsertSameIdCount > 0 || t.InsertNewIdCount > 0);

    /// <summary>Gets the total number of rows that would be inserted across all tables.</summary>
    public int TotalInsertCount => TablePreviews.Sum(t => t.InsertSameIdCount + t.InsertNewIdCount);
}
