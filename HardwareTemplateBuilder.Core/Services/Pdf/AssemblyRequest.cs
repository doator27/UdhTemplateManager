using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Pairs a hardware item with the templates linked to it for PDF assembly purposes.
/// </summary>
public class HardwareWithTemplates
{
    /// <summary>Gets or sets the hardware item. Navigation properties must be loaded.</summary>
    public HardwareItem Item { get; init; } = null!;

    /// <summary>
    /// Gets or sets the templates linked to this item, with their navigation properties loaded
    /// (<c>Manufacturer</c>, <c>Description.WeightValue</c>).
    /// </summary>
    public IReadOnlyList<IndividualTemplate> Templates { get; init; } = Array.Empty<IndividualTemplate>();

    /// <summary>
    /// Gets or sets the optional custom label for this hardware row on the cover sheet.
    /// When set, overrides the item's model number in the <c>HardwareDescription</c> column.
    /// </summary>
    public string? CustomDescription { get; init; }
}

/// <summary>
/// Input parameters for <see cref="PdfAssemblyService.AssembleAsync"/>.
/// </summary>
public class AssemblyRequest
{
    /// <summary>
    /// Gets or sets the job being assembled. Navigation properties
    /// (<c>Customer</c>, <c>ProjectManager</c>) must be loaded.
    /// </summary>
    public Job Job { get; init; } = null!;

    /// <summary>
    /// Gets or sets the hardware items and their templates in job display order.
    /// </summary>
    public IReadOnlyList<HardwareWithTemplates> Hardware { get; init; } = Array.Empty<HardwareWithTemplates>();

    /// <summary>
    /// Gets or sets the directory under which the job subfolder and final PDF are written.
    /// </summary>
    public string OutputDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets a flat dictionary of every <see cref="Description"/> record keyed by ID.
    /// Used by the sort strategy to compute full ancestor-path sort keys.
    /// </summary>
    public IReadOnlyDictionary<int, Description> AllDescriptions { get; init; } =
        new Dictionary<int, Description>();

    /// <summary>
    /// Gets or sets the display name of the user generating this package,
    /// printed on the cover sheet as "Templates by: {name}".
    /// </summary>
    public string PreparedByName { get; init; } = string.Empty;
}

/// <summary>
/// Holds the data required to create one <c>JobTemplateSnapshot</c> database record after
/// a successful assembly. Returned by <see cref="PdfAssemblyService.AssembleAsync"/> so
/// that callers can persist snapshots without duplicating acquisition logic.
/// </summary>
public class TemplateSnapshotInfo
{
    /// <summary>Gets or sets the ID of the source <c>IndividualTemplate</c>.</summary>
    public int IndividualTemplateId { get; init; }

    /// <summary>Gets or sets the full path to the acquired (copied or downloaded) file.</summary>
    public string AcquiredFilePath { get; init; } = string.Empty;

    /// <summary>Gets or sets the page range string copied from the template at snapshot time.</summary>
    public string PagesToPrint { get; init; } = string.Empty;

    /// <summary>Gets or sets the rotation page range copied from the template (null if no rotation).</summary>
    public string? PagesToRotate { get; init; }

    /// <summary>Gets or sets the rotation angle copied from the template.</summary>
    public int RotationDirection { get; init; }
}

/// <summary>
/// Output from a successful <see cref="PdfAssemblyService.AssembleAsync"/> call.
/// </summary>
public class AssemblyResult
{
    /// <summary>Gets or sets the full path to the generated PDF package.</summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>Gets or sets the number of pages the cover sheet occupies.</summary>
    public int CoverSheetPageCount { get; init; }

    /// <summary>
    /// Gets or sets the per-template snapshot data collected during assembly, one entry
    /// per template in sort order. Used by callers to write <c>JobTemplateSnapshot</c> records.
    /// </summary>
    public IReadOnlyList<TemplateSnapshotInfo> TemplateSnapshots { get; init; } =
        Array.Empty<TemplateSnapshotInfo>();
}
