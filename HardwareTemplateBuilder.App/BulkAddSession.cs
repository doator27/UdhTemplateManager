using System.Collections.Generic;
using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.App;

/// <summary>
/// Represents one row entered in <see cref="Views.BulkHardwareEntryView"/>.
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

    /// <summary>Gets or sets the optional custom label for the job link.</summary>
    public string? CustomLabel { get; set; }

    /// <summary>Gets or sets optional remarks to store on the hardware item.</summary>
    public string? Remarks { get; set; }

    /// <summary>
    /// Gets or sets the existing <see cref="HardwareItem"/> that exactly matches this row,
    /// or <c>null</c> if this will be a new item.
    /// </summary>
    public HardwareItem? MatchedItem { get; set; }

    /// <summary>
    /// Gets or sets the templates staged for a new (Case B) hardware item.
    /// Populated during the wizard before the item is persisted.
    /// </summary>
    public List<IndividualTemplate> PendingTemplates { get; set; } = new();

    /// <summary>
    /// Gets or sets the hardware item record created during the wizard for Case B rows.
    /// Set once <see cref="HardwareItemRepository.Add"/> has been called.
    /// </summary>
    public HardwareItem? CreatedItem { get; set; }
}

/// <summary>
/// Static session state shared between <see cref="Views.BulkHardwareEntryView"/> and
/// <see cref="Views.TemplateResolutionWizardView"/>. Mirrors the pattern used by
/// <see cref="SessionService"/>.
/// </summary>
public static class BulkAddSession
{
    /// <summary>Gets or sets the rows to be processed by the wizard.</summary>
    public static List<BulkHardwareRow> PendingRows { get; set; } = new();

    /// <summary>Gets or sets the job ID that the rows will be linked to.</summary>
    public static int JobId { get; set; }
}
