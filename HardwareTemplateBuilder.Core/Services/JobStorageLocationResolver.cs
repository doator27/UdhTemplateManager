using System;
using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// The resolved save location(s) for a job's output (packages, cover sheets, attachments,
/// old-version backups, and history) for a particular user.
/// </summary>
public class JobStorageLocation
{
    /// <summary>
    /// Gets the primary root folder job output should be written to: the user's
    /// <see cref="UserProfile.CustomJobSaveLocation"/> when set, otherwise the shared
    /// App Settings <c>TemplateStorageLocation</c>.
    /// </summary>
    public string PrimaryRoot { get; init; } = string.Empty;

    /// <summary>
    /// Gets the shared App Settings root folder that a copy of the job should also be written
    /// to, or <c>null</c> when it is the same as <see cref="PrimaryRoot"/> (i.e. no distinct
    /// custom job location is configured, so there is nothing extra to mirror).
    /// </summary>
    public string? SecondaryRoot { get; init; }
}

/// <summary>
/// Resolves where a job's output should be saved: a user's optional custom job save location
/// (primary) plus the shared App Settings template storage location (secondary, when different),
/// so a copy is always retained in the shared location. Templates themselves are unaffected by
/// this feature and always use the App Settings location directly.
/// </summary>
public static class JobStorageLocationResolver
{
    /// <summary>
    /// Resolves the primary/secondary job save roots for the given user profile.
    /// </summary>
    /// <param name="appSettingsStorageDir">
    /// The shared App Settings <c>TemplateStorageLocation</c> value, already resolved to a
    /// non-empty fallback (e.g. My Documents) by the caller.
    /// </param>
    /// <param name="userProfile">
    /// The job's owning user profile, or <c>null</c> when it could not be resolved (in which
    /// case the App Settings location is used for both primary and secondary, i.e. no mirroring).
    /// </param>
    public static JobStorageLocation Resolve(string appSettingsStorageDir, UserProfile? userProfile)
    {
        var custom = userProfile?.CustomJobSaveLocation?.Trim();

        if (string.IsNullOrEmpty(custom) ||
            PathsEqual(custom, appSettingsStorageDir))
        {
            return new JobStorageLocation { PrimaryRoot = appSettingsStorageDir, SecondaryRoot = null };
        }

        return new JobStorageLocation { PrimaryRoot = custom, SecondaryRoot = appSettingsStorageDir };
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
