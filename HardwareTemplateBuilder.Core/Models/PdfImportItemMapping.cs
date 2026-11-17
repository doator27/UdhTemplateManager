using System;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// What the "Parse PDF into Bulk Add" import has learned about one PDF callout: a manufacturer
/// code + catalog text (by <see cref="SourceKey"/>) the user confirmed as a hardware item and
/// label, or as "skip". Once <see cref="ConfirmCount"/> reaches the auto-match threshold
/// (App Setting <c>PdfImportAutoMatchThreshold</c>, default 5) the import applies it without asking.
/// Confirming a different answer replaces the target and restarts the count.
/// </summary>
public class PdfImportItemMapping
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the manufacturer code as printed in the PDF (cleaned), or empty.</summary>
    public string ManufacturerCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the comparison key of the catalog text (see <c>ImportText.Key</c>).</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the catalog text as last seen, for display.</summary>
    public string SourceText { get; set; } = string.Empty;

    /// <summary>Gets or sets the hardware item it maps to (null for skip, or for an item that didn't exist yet).</summary>
    public int? HardwareItemId { get; set; }

    /// <summary>Gets or sets the target manufacturer (with <see cref="TargetModelNumber"/>, finds the item if it was created later).</summary>
    public int? TargetManufacturerId { get; set; }

    /// <summary>Gets or sets the target model number.</summary>
    public string? TargetModelNumber { get; set; }

    /// <summary>Gets or sets the label to use on the job (null/empty = no label).</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets whether the user chose to skip this callout.</summary>
    public bool IsSkip { get; set; }

    /// <summary>Gets or sets how many times the user has confirmed this answer.</summary>
    public int ConfirmCount { get; set; }

    /// <summary>Gets or sets when it was last confirmed (UTC).</summary>
    public DateTime LastConfirmedUtc { get; set; }
}
