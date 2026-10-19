using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>Describes the planned (or applied) outcome for one hardware-item/template pairing.</summary>
public sealed class RelinkRowResult
{
    /// <summary>Gets the hardware item's Id in the master database.</summary>
    public int HardwareItemId { get; init; }

    /// <summary>Gets a human-readable description of the hardware item (manufacturer + description + model).</summary>
    public string HardwareItemDescription { get; init; } = string.Empty;

    /// <summary>Gets the individual template's Id in the master database.</summary>
    public int IndividualTemplateId { get; init; }

    /// <summary>Gets a human-readable description of the template (manufacturer + description + template number).</summary>
    public string TemplateDescription { get; init; } = string.Empty;
}

/// <summary>
/// The plan produced by <see cref="TemplateRelinkService.PreviewAsync"/>, describing which
/// missing hardware-item/template links would be restored in the master database by matching
/// hardware items and templates against a legacy SQLite database on Manufacturer + Description
/// path + Model/Template number (ignoring Ids, which may differ between databases).
/// </summary>
public sealed class TemplateRelinkPlan
{
    /// <summary>Gets the path to the legacy SQLite database file this plan was computed from.</summary>
    public string SourceFilePath { get; init; } = string.Empty;

    /// <summary>Gets the links that already exist in the master database and would be skipped.</summary>
    public List<RelinkRowResult> AlreadyLinked { get; init; } = new();

    /// <summary>Gets the links that are missing in the master database and would be added.</summary>
    public List<RelinkRowResult> ToAdd { get; init; } = new();

    /// <summary>
    /// Gets hardware items present in the legacy database that could not be matched to a
    /// hardware item in the master database (by Manufacturer + Description path + Model Number),
    /// so their old links could not be checked.
    /// </summary>
    public List<string> UnmatchedHardwareItems { get; init; } = new();

    /// <summary>
    /// Gets legacy template links that reference a template which could not be matched to a
    /// template in the master database (by Manufacturer + Description path + Template Number).
    /// </summary>
    public List<string> UnmatchedTemplates { get; init; } = new();

    /// <summary>Gets whether this plan contains any links that would actually be added.</summary>
    public bool HasChanges => ToAdd.Count > 0;

    /// <summary>Gets a one-line summary of the plan.</summary>
    public string Summary =>
        $"{ToAdd.Count} link(s) to restore, {AlreadyLinked.Count} already linked, " +
        $"{UnmatchedHardwareItems.Count} hardware item(s) unmatched, {UnmatchedTemplates.Count} template(s) unmatched";
}
