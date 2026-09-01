using System.Collections.Generic;
using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.App;

/// <summary>
/// A single custom-label entry for a hardware item on a bulk-add row.
/// One <see cref="BulkHardwareLabel"/> becomes one <see cref="JobHardware"/> record.
/// </summary>
public class BulkHardwareLabel
{
    /// <summary>Gets or sets the custom label for this line item.</summary>
    public string CustomLabel { get; set; } = "";

    /// <summary>Gets or sets optional remarks displayed alongside the label on the cover sheet.</summary>
    public string? Remarks { get; set; }
}

/// <summary>
/// Wraps a staged <see cref="IndividualTemplate"/> together with the job-scoping flag
/// set by the user in the template resolution wizard.
/// </summary>
public class BulkPendingTemplate
{
    /// <summary>Gets or sets the staged template (may be a deduped existing record).</summary>
    public IndividualTemplate Template { get; set; } = null!;

    /// <summary>
    /// Gets or sets whether this template link should be scoped to the current job only.
    /// When true, <c>HardwareItemTemplate.JobId</c> is set to the current job on Finish.
    /// </summary>
    public bool IsJobSpecific { get; set; }

    /// <summary>
    /// Gets a display string for list binding: template number with an optional "[job]" suffix.
    /// </summary>
    public string DisplayText =>
        IsJobSpecific ? $"{Template.TemplateNumber} [job]" : Template.TemplateNumber;
}

/// <summary>
/// Represents one row entered in the bulk hardware entry view.
/// Carries the user's input plus resolved state for the wizard.
/// </summary>
public class BulkHardwareRow
{
    /// <summary>Gets or sets the manufacturer selected for this row.</summary>
    public Manufacturer? SelectedManufacturer { get; set; }

    /// <summary>Gets or sets the description selected via the picker for this row.</summary>
    public Description? SelectedDescription { get; set; }

    /// <summary>Gets or sets the model number typed by the user.</summary>
    public string ModelNumber { get; set; } = "";

    /// <summary>Gets or sets the standard remarks for the hardware item (stored on <see cref="HardwareItem.Remarks"/>).</summary>
    public string? HardwareItemRemarks { get; set; }

    /// <summary>
    /// Gets or sets the list of custom labels for this row.
    /// Each label becomes a distinct <see cref="JobHardware"/> line item on the job.
    /// </summary>
    public List<BulkHardwareLabel> Labels { get; set; } = new();

    /// <summary>
    /// Gets or sets the existing <see cref="HardwareItem"/> that exactly matches this row,
    /// or <c>null</c> if this will be a new item.
    /// </summary>
    public HardwareItem? MatchedItem { get; set; }

    /// <summary>
    /// Gets or sets the templates staged for a new (Case B) hardware item.
    /// Each entry carries the template and a job-scoping flag set by the user.
    /// </summary>
    public List<BulkPendingTemplate> PendingTemplates { get; set; } = new();

    /// <summary>
    /// Gets or sets the hardware item record created during the wizard for Case B rows.
    /// Set once <see cref="HardwareItemRepository.Add"/> has been called.
    /// </summary>
    public HardwareItem? CreatedItem { get; set; }
}

/// <summary>
/// Static session state shared between the bulk-add views and the template resolution wizard.
/// </summary>
public static class BulkAddSession
{
    /// <summary>Gets or sets the rows to be processed by the wizard.</summary>
    public static List<BulkHardwareRow> PendingRows { get; set; } = new();

    /// <summary>Gets or sets the job ID that the rows will be linked to.</summary>
    public static int JobId { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="JobRelease"/> ID that newly-added hardware should be scoped to.
    /// Null means the base (no-release) hardware list. Set from the currently selected release on
    /// <see cref="HardwareTemplateBuilder.App.Views.JobDetailView"/> before entering the bulk-add flow.
    /// </summary>
    public static int? ReleaseId { get; set; }

    /// <summary>
    /// Gets or sets the ordered list of manufacturers selected for the current bulk-add session.
    /// Populated by <c>BulkManufacturerSelectionView</c> and consumed by the hub and item entry views.
    /// </summary>
    public static List<Manufacturer> SelectedManufacturers { get; set; } = new();
}
