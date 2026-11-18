namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>One hardware line read from a PDF (a schedule/spec item line or a costing line).</summary>
public sealed class ParsedHardwareLine
{
    /// <summary>Gets or sets the manufacturer code/abbreviation as printed (cleaned), e.g. "IV", "MK01"; empty if none.</summary>
    public string ManufacturerCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the manufacturer name from the PDF's own code list, when it has one.</summary>
    public string? ManufacturerName { get; set; }

    /// <summary>Gets or sets the generic item name, e.g. "Hinges", "Closer".</summary>
    public string GenericName { get; set; } = string.Empty;

    /// <summary>Gets or sets the catalog/model callout without handing and finish, e.g. "4040XP REGARM".</summary>
    public string Catalog { get; set; } = string.Empty;

    /// <summary>Gets or sets the handing, if printed.</summary>
    public string? Handing { get; set; }

    /// <summary>Gets or sets the finish, if printed.</summary>
    public string? Finish { get; set; }

    /// <summary>Gets or sets the quantity on the line (0 if unknown).</summary>
    public int Quantity { get; set; }

    /// <summary>Gets or sets the hardware set / heading (or openings) the line belongs to.</summary>
    public string? SetName { get; set; }

    /// <summary>Gets or sets the line's extended (total) cost on a costing report, if printed.</summary>
    public decimal? ExtendedCost { get; set; }

    /// <summary>Gets or sets the 1-based page number.</summary>
    public int Page { get; set; }
}
