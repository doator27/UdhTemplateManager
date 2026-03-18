using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a construction job that groups hardware items into a PDF package.
/// </summary>
public class Job
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the job number identifier (e.g., "2024-001").</summary>
    [Required]
    public string JobNumber { get; set; } = string.Empty;

    /// <summary>Gets or sets the descriptive name of the job.</summary>
    [Required]
    public string JobName { get; set; } = string.Empty;

    /// <summary>Gets or sets the foreign key to the customer.</summary>
    public int CustomerId { get; set; }

    /// <summary>Gets or sets the customer associated with this job.</summary>
    public Customer Customer { get; set; } = null!;

    /// <summary>Gets or sets the foreign key to the project manager.</summary>
    public int ProjectManagerId { get; set; }

    /// <summary>Gets or sets the project manager responsible for this job.</summary>
    public ProjectManager ProjectManager { get; set; } = null!;

    /// <summary>Gets or sets the foreign key to the user profile that created this job.</summary>
    public int UserProfileId { get; set; }

    /// <summary>Gets or sets the user profile that created this job.</summary>
    public UserProfile UserProfile { get; set; } = null!;

    /// <summary>Gets or sets the junction records linking hardware items to this job.</summary>
    public ICollection<JobHardware> JobHardwareLinks { get; set; } = new List<JobHardware>();

    /// <summary>Gets or sets the template snapshots captured when PDF packages were generated for this job.</summary>
    public ICollection<JobTemplateSnapshot> Snapshots { get; set; } = new List<JobTemplateSnapshot>();

    /// <summary>Gets or sets the files (emails and PDFs) attached to this job.</summary>
    public ICollection<JobAttachment> Attachments { get; set; } = new List<JobAttachment>();
}
