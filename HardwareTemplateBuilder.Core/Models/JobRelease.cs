using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a named revision of a job's hardware list.
/// A job can have multiple releases; <see cref="JobHardware"/> rows with a null
/// <c>ReleaseId</c> belong to the base list, while non-null rows belong to a specific release.
/// </summary>
public class JobRelease
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the foreign key to the parent job.</summary>
    public int JobId { get; set; }

    /// <summary>Gets or sets the parent job.</summary>
    public Job Job { get; set; } = null!;

    /// <summary>Gets or sets the 1-based release number within the job.</summary>
    public int ReleaseNumber { get; set; }

    /// <summary>Gets or sets the short label shown in the release selector (e.g. "Addendum 1").</summary>
    [Required]
    public string ReleaseLabel { get; set; } = string.Empty;

    /// <summary>Gets or sets optional notes about this release.</summary>
    public string? Notes { get; set; }

    /// <summary>Gets or sets when this release was created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets the hardware rows belonging to this release.</summary>
    public ICollection<JobHardware> HardwareLinks { get; set; } = new List<JobHardware>();

    /// <summary>Gets the display label for the release selector combo.</summary>
    public string DisplayLabel => $"Release {ReleaseNumber}: {ReleaseLabel}";
}
