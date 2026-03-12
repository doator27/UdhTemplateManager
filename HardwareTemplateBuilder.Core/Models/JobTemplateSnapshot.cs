using System;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Captures the exact state of a template at the time a job PDF package was generated.
/// These records are immutable — never modified after creation.
/// </summary>
public class JobTemplateSnapshot
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the foreign key to the job this snapshot belongs to.</summary>
    public int JobId { get; set; }

    /// <summary>Gets or sets the associated job.</summary>
    public Job Job { get; set; } = null!;

    /// <summary>Gets or sets the foreign key to the original template record.</summary>
    public int IndividualTemplateId { get; set; }

    /// <summary>Gets or sets the original template this snapshot was captured from.</summary>
    public IndividualTemplate IndividualTemplate { get; set; } = null!;

    /// <summary>Gets or sets the file path used for this template in the generated package.</summary>
    [Required]
    public string SnapshotLocalLink { get; set; } = string.Empty;

    /// <summary>Gets or sets the UTC date and time when this snapshot was created.</summary>
    public DateTime SnapshotDate { get; set; }

    /// <summary>Gets or sets the pages that were extracted, copied from IndividualTemplate at snapshot time.</summary>
    [Required]
    public string PagesToPrint { get; set; } = string.Empty;

    /// <summary>Gets or sets the pages that were rotated, copied from IndividualTemplate at snapshot time.</summary>
    public string? PagesToRotate { get; set; }

    /// <summary>Gets or sets the rotation angle applied, copied from IndividualTemplate at snapshot time.</summary>
    public int RotationDirection { get; set; }
}
