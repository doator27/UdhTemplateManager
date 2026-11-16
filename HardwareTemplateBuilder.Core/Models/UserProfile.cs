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
    /// Gets or sets this user's job save location: the root folder for their job output
    /// (packages, cover sheets, attachments, old-version backups, and history), e.g. a folder on
    /// the server. When set, it is the primary job save location, and a copy is also written to
    /// the shared App Settings <c>TemplateStorageLocation</c> if that is configured. Null/empty
    /// means "use the App Settings location" (or My Documents if that is unset too). Templates
    /// (not jobs) always use the App Settings location regardless of this setting. Jobs saved
    /// elsewhere on a computer can be moved here with <c>JobFolderMigrationService</c>.
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
