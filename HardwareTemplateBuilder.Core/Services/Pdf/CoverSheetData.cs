namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// All data required to render a cover sheet for a PDF template package.
/// </summary>
public class CoverSheetData
{
    /// <summary>Gets or sets the job number (e.g. "2024-001").</summary>
    public string JobNumber { get; set; } = string.Empty;

    /// <summary>Gets or sets the descriptive name of the job.</summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>Gets or sets the customer name.</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the project manager's name.</summary>
    public string ProjectManagerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the date the package was generated.</summary>
    public DateTime DateCreated { get; set; }

    /// <summary>Gets or sets the name of the user who generated the package.</summary>
    public string PreparedBy { get; set; } = string.Empty;

    /// <summary>Gets or sets one row per hardware item on the job.</summary>
    public IReadOnlyList<CoverSheetRow> Rows { get; set; } = Array.Empty<CoverSheetRow>();
}

/// <summary>
/// A single hardware-item row in the cover sheet table.
/// </summary>
public class CoverSheetRow
{
    /// <summary>Gets or sets the manufacturer name.</summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>Gets or sets the hardware type (description category, e.g. "Closer").</summary>
    public string HardwareType { get; set; } = string.Empty;

    /// <summary>Gets or sets the hardware description (model number).</summary>
    public string HardwareDescription { get; set; } = string.Empty;

    /// <summary>Gets or sets the comma-separated template numbers (e.g. "T-001, T-002").</summary>
    public string TemplateNumbers { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the page numbers in the assembled body PDF where this item's templates
    /// appear (e.g. "1-3, 5"). These are the numbers stamped by <see cref="PageNumberer"/>.
    /// </summary>
    public string PageNumbers { get; set; } = string.Empty;

    /// <summary>Gets or sets optional remarks for this hardware item.</summary>
    public string? Remarks { get; set; }
}
