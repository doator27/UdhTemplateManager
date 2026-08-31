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

    /// <summary>
    /// Gets or sets this user's custom root folder for saving job output (packages, cover
    /// sheets, attachments, old-version backups, and history). When set, it is used as the
    /// primary job save location and a copy of everything is still written to the shared
    /// App Settings <c>TemplateStorageLocation</c>. Null/empty means "use the App Settings
    /// location only", matching prior behavior. Templates (not jobs) always use the App
    /// Settings location regardless of this setting.
    /// </summary>
    public string? CustomJobSaveLocation { get; set; }

    /// <summary>
    /// Gets or sets the stable machine identifier this profile is bound to.
    /// Null until the profile has been used on at least one machine.
    /// </summary>
    public string? MachineId { get; set; }

    /// <summary>Gets or sets the jobs created by this user.</summary>
    public ICollection<Job> Jobs { get; set; } = new List<Job>();
}
