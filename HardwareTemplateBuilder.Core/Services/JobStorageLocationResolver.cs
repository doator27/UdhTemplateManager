using System;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// The resolved save location(s) for a job's output (packages, cover sheets, attachments,
/// old-version backups, and history) for a particular user.
/// </summary>
public class JobStorageLocation
{
    /// <summary>
    /// Gets the primary root folder job output is written to: the user's
    /// <see cref="UserProfile.CustomJobSaveLocation"/> when set, otherwise the shared
    /// App Settings <c>TemplateStorageLocation</c>, otherwise My Documents as a last resort.
    /// </summary>
    public string PrimaryRoot { get; init; } = string.Empty;

    /// <summary>
    /// Gets the shared App Settings root folder that a copy of the job should also be written
    /// to, or <c>null</c> when there is nothing distinct to mirror to: no custom job location is
    /// set, the shared location is the same folder, or the shared location isn't configured
    /// (a blank App Settings value must never cause copies to be written to My Documents).
    /// </summary>
    public string? SecondaryRoot { get; init; }

    /// <summary>
    /// Gets whether <see cref="PrimaryRoot"/> came from the user's own job save location
    /// (rather than the shared or My Documents fallback).
    /// </summary>
    public bool IsUserLocation { get; init; }
}

/// <summary>
/// Resolves where a job's output should be saved. The job owner's
/// <see cref="UserProfile.CustomJobSaveLocation"/> wins; otherwise the shared App Settings
/// <c>TemplateStorageLocation</c>; otherwise My Documents. Templates themselves are unaffected
/// and always use the App Settings location directly.
/// </summary>
public static class JobStorageLocationResolver
{
    /// <summary>The App Settings key holding the shared template (and default job) folder.</summary>
    public const string SharedLocationSettingKey = "TemplateStorageLocation";

    /// <summary>The last-resort root used when neither a user nor a shared location is configured.</summary>
    public static string FallbackRoot => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>
    /// Resolves the save location for a job, using the job's owning user profile and the
    /// App Settings shared location read from <paramref name="context"/>.
    /// </summary>
    public static JobStorageLocation ResolveForJob(AppDbContext context, Job? job)
    {
        var profile = job != null ? context.UserProfiles.Find(job.UserProfileId) : null;
        return ResolveForProfile(context, profile);
    }

    /// <summary>
    /// Resolves the save location for jobs owned by <paramref name="userProfile"/>, reading the
    /// App Settings shared location from <paramref name="context"/>.
    /// </summary>
    public static JobStorageLocation ResolveForProfile(AppDbContext context, UserProfile? userProfile)
    {
        var shared = new AppSettingRepository(context).GetValue(SharedLocationSettingKey);
        return Resolve(shared, userProfile, FallbackRoot);
    }

    /// <summary>
    /// Resolves the primary/secondary job save roots.
    /// </summary>
    /// <param name="sharedLocation">
    /// The raw App Settings <c>TemplateStorageLocation</c> value; null/blank when not configured.
    /// </param>
    /// <param name="userProfile">The job's owning user profile, or <c>null</c> if unknown.</param>
    /// <param name="fallbackRoot">Used only when neither the user nor the shared location is set.</param>
    public static JobStorageLocation Resolve(string? sharedLocation, UserProfile? userProfile, string fallbackRoot)
    {
        var custom = userProfile?.CustomJobSaveLocation?.Trim();
        var shared = sharedLocation?.Trim();

        if (!string.IsNullOrEmpty(custom))
        {
            var mirror = !string.IsNullOrEmpty(shared) && !PathsEqual(custom, shared) ? shared : null;
            return new JobStorageLocation { PrimaryRoot = custom, SecondaryRoot = mirror, IsUserLocation = true };
        }

        return new JobStorageLocation
        {
            PrimaryRoot = !string.IsNullOrEmpty(shared) ? shared : fallbackRoot,
            SecondaryRoot = null,
        };
    }

    /// <summary>True when two folder paths refer to the same folder (case- and trailing-slash-insensitive).</summary>
    public static bool PathsEqual(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('\\', '/'), b.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
