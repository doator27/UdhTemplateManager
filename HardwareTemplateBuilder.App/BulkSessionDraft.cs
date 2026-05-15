using System.Collections.Generic;

namespace HardwareTemplateBuilder.App;

/// <summary>
/// Root object serialised to <c>BulkAddDraft.DraftJson</c> for the manufacturer-session bulk flow.
/// One draft exists per job and spans all manufacturers in the session.
/// </summary>
public sealed class BulkSessionDraft
{
    /// <summary>Gets or sets the manufacturers added to this session, in entry order.</summary>
    public List<BulkSessionMfr> Manufacturers { get; set; } = new();
}

/// <summary>One manufacturer's worth of hardware items within a bulk session.</summary>
public sealed class BulkSessionMfr
{
    /// <summary>Gets or sets the <see cref="HardwareTemplateBuilder.Core.Models.Manufacturer"/> primary key.</summary>
    public int ManufacturerId { get; set; }

    /// <summary>Gets or sets the manufacturer display name (for display without a DB round-trip).</summary>
    public string ManufacturerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the hardware item groups entered for this manufacturer.</summary>
    public List<BulkSessionGroup> Groups { get; set; } = new();
}

/// <summary>One hardware item (description + model) with its template and callout lines.</summary>
public sealed class BulkSessionGroup
{
    /// <summary>Gets or sets the selected description ID (null if not yet chosen).</summary>
    public int? DescriptionId { get; set; }

    /// <summary>Gets or sets the typed model number.</summary>
    public string ModelNumber { get; set; } = string.Empty;

    /// <summary>Gets or sets the template number linked to this hardware item (optional).</summary>
    public string? TemplateNumber { get; set; }

    /// <summary>
    /// Gets or sets the item-level remarks for the hardware item itself.
    /// These are shared across all jobs and stored in <see cref="Core.Models.HardwareItem.Remarks"/>.
    /// </summary>
    public string? HardwareItemRemarks { get; set; }

    /// <summary>Gets or sets the per-callout rows. At least one entry is expected.</summary>
    public List<BulkSessionCallout> Callouts { get; set; } = new();
}

/// <summary>One callout row: a custom label plus an optional per-callout remark.</summary>
public sealed class BulkSessionCallout
{
    /// <summary>Gets or sets the custom label shown as the hardware description on the cover sheet.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the per-callout remark shown alongside the item remark on the cover sheet.</summary>
    public string? CalloutRemarks { get; set; }
}
