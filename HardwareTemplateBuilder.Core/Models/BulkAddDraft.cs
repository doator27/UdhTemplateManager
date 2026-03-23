using System;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Persists an in-progress bulk-hardware-entry session so the user can
/// leave the view and return without losing their work.
/// One row per job — replaced wholesale on each auto-save.
/// </summary>
public class BulkAddDraft
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the job this draft belongs to.</summary>
    public int JobId { get; set; }

    /// <summary>Gets or sets the parent job.</summary>
    public Job Job { get; set; } = null!;

    /// <summary>Gets or sets the JSON-serialised list of BulkHardwareRow objects.</summary>
    [Required]
    public string DraftJson { get; set; } = "[]";

    /// <summary>Gets or sets when this draft was last saved (UTC).</summary>
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}
