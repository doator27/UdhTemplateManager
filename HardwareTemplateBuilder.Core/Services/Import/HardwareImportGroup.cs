using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>How a distinct PDF callout was resolved.</summary>
public enum ImportMatchStatus
{
    /// <summary>Identified automatically (exact model number, a label used before, or learned).</summary>
    Matched,

    /// <summary>Not identified (or not yet trusted): the wizard asks the user.</summary>
    NeedsReview,

    /// <summary>Learned as "skip" (e.g. keys, cores, items by others): left out.</summary>
    Skipped,
}

/// <summary>A possible hardware item for a callout, offered in the wizard.</summary>
/// <param name="HardwareItemId">The item.</param>
/// <param name="Label">The label to use with it (null = no label).</param>
/// <param name="Reason">Why it was suggested, for display.</param>
/// <param name="Score">Ranking score (higher first).</param>
public sealed record ImportSuggestion(int HardwareItemId, string? Label, string Reason, int Score);

/// <summary>
/// One distinct callout from the PDF (same manufacturer code and catalog number; handing and
/// finish ignored), with every line it came from and how it was resolved. The wizard edits the
/// "chosen" fields of groups that need review.
/// </summary>
public sealed class HardwareImportGroup
{
    /// <summary>Gets or sets the cleaned manufacturer code from the PDF.</summary>
    public string ManufacturerCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the manufacturer name the PDF gives for the code, if any.</summary>
    public string? PdfManufacturerName { get; set; }

    /// <summary>Gets or sets the generic item name from the PDF.</summary>
    public string GenericName { get; set; } = string.Empty;

    /// <summary>Gets or sets the catalog text (first occurrence).</summary>
    public string Catalog { get; set; } = string.Empty;

    /// <summary>Gets or sets the comparison key of <see cref="Catalog"/>.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>Gets the PDF lines this callout appears on.</summary>
    public List<ParsedHardwareLine> Lines { get; } = new();

    /// <summary>Gets the total quantity across all lines.</summary>
    public int TotalQuantity => Lines.Sum(l => l.Quantity);

    /// <summary>Gets the distinct sets/headings it appears in.</summary>
    public IReadOnlyList<string> Sets => Lines.Select(l => l.SetName).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList()!;

    /// <summary>Gets or sets the manufacturer the code resolved to (null = unknown).</summary>
    public int? ManufacturerId { get; set; }

    /// <summary>Gets or sets the status.</summary>
    public ImportMatchStatus Status { get; set; }

    /// <summary>Gets or sets why it was matched/skipped, for display.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets the suggestions, best first.</summary>
    public List<ImportSuggestion> Suggestions { get; } = new();

    /// <summary>Gets or sets the chosen hardware item (existing item).</summary>
    public int? HardwareItemId { get; set; }

    /// <summary>Gets or sets the chosen label (empty = no label).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the user chose to add this as a new hardware item.</summary>
    public bool CreateNew { get; set; }

    /// <summary>Gets or sets the new item's manufacturer.</summary>
    public int? NewManufacturerId { get; set; }

    /// <summary>Gets or sets the new item's description.</summary>
    public int? NewDescriptionId { get; set; }

    /// <summary>Gets or sets the new item's model number.</summary>
    public string NewModelNumber { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the user chose to leave this callout out.</summary>
    public bool Skip { get; set; }

    /// <summary>One-line description for lists: "IV  Hinges  5BB1 4 1/2 x 4 1/2".</summary>
    public string DisplayText => $"{ManufacturerCode}  {GenericName}  {Catalog}".Trim();
}
