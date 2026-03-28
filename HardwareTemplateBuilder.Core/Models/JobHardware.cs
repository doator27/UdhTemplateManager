using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Junction table linking hardware items to jobs.
/// </summary>
public class JobHardware
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the foreign key to the job.</summary>
    public int JobId { get; set; }

    /// <summary>Gets or sets the associated job.</summary>
    public Job Job { get; set; } = null!;

    /// <summary>Gets or sets the foreign key to the hardware item.</summary>
    public int HardwareItemId { get; set; }

    /// <summary>Gets or sets the associated hardware item.</summary>
    public HardwareItem HardwareItem { get; set; } = null!;

    /// <summary>
    /// Gets or sets an optional custom label for this hardware item on this job.
    /// When set, it overrides the item's model number on the cover sheet.
    /// </summary>
    public string? CustomDescription { get; set; }

    /// <summary>
    /// Gets or sets an optional per-callout remark specific to this job line item.
    /// Shown on the cover sheet alongside <c>HardwareItem.Remarks</c>.
    /// </summary>
    public string? CalloutRemarks { get; set; }

    /// <summary>
    /// Gets or sets the optional foreign key to the <see cref="JobRelease"/> this row belongs to.
    /// Null means the row belongs to the base (original) hardware list.
    /// </summary>
    public int? ReleaseId { get; set; }

    /// <summary>Gets or sets the release this row belongs to (null = base list).</summary>
    public JobRelease? Release { get; set; }

    /// <summary>
    /// Returns the custom description if set; otherwise the linked item's model number.
    /// Used as the display label in the UI linked-hardware list.
    /// </summary>
    public string DisplayLabel =>
        !string.IsNullOrWhiteSpace(CustomDescription)
            ? CustomDescription
            : HardwareItem?.ModelNumber ?? $"Item #{HardwareItemId}";
}
