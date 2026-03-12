using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents an application user profile with save location preferences.
/// </summary>
public class UserProfile
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the user's display name.</summary>
    [Required]
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the default folder path for saving downloaded templates.</summary>
    [Required]
    public string DefaultTemplateSaveLocation { get; set; } = string.Empty;

    /// <summary>Gets or sets the jobs created by this user.</summary>
    public ICollection<Job> Jobs { get; set; } = new List<Job>();
}
