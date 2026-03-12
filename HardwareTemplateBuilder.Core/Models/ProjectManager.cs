using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a project manager who can be assigned to jobs.
/// </summary>
public class ProjectManager
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the project manager's name.</summary>
    [Required]
    public string ProjectManagerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the jobs managed by this project manager.</summary>
    public ICollection<Job> Jobs { get; set; } = new List<Job>();
}
