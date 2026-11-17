using System;
using System.Linq;
using System.Text;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// How a job's releases are named on generated output. The base list (no release) prints the
/// plain job number; a release prints "{JobNumber} {ReleaseLabel}" (e.g. "24117 Addendum 1").
/// The job name and other job properties are never changed by a release. Used by
/// <see cref="JobPackageGenerationService"/> and the cover-sheet-only actions in both apps.
/// </summary>
public static class JobReleaseNaming
{
    /// <summary>File-name prefix of every generated package PDF in a job folder.</summary>
    public const string PackageFilePrefix = "Templates for job ";

    /// <summary>
    /// The job number printed on the cover sheet: the plain job number for the base list
    /// (<paramref name="releaseLabel"/> null/blank), otherwise the job number and release label
    /// separated by a space.
    /// </summary>
    /// <param name="jobNumber">The job's number.</param>
    /// <param name="releaseLabel">The active release's label, or null for the base list.</param>
    public static string CoverJobNumber(string jobNumber, string? releaseLabel) =>
        string.IsNullOrWhiteSpace(releaseLabel) ? jobNumber : $"{jobNumber} {releaseLabel.Trim()}";

    /// <summary>
    /// The package PDF's file name for a cover-sheet job number, e.g.
    /// "Templates for job 24117 Addendum 1.pdf". Each release gets its own file, so generating
    /// one release never overwrites another's package.
    /// </summary>
    /// <param name="coverJobNumber">The value returned by <see cref="CoverJobNumber"/>.</param>
    public static string PackageFileName(string coverJobNumber) =>
        $"{PackageFilePrefix}{SafeFileNamePart(coverJobNumber)}.pdf";

    /// <summary>True when <paramref name="fileName"/> is a generated package PDF (any release).</summary>
    /// <param name="fileName">A file name (no folder).</param>
    public static bool IsPackageFile(string fileName) =>
        fileName.StartsWith(PackageFilePrefix, StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces characters Windows doesn't allow in file names (checked explicitly so the result
    /// is the same when this runs on Linux) with '-'.
    /// </summary>
    /// <param name="value">Text to use inside a file name.</param>
    public static string SafeFileNamePart(string value)
    {
        const string invalid = "<>:\"/\\|?*";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(c < 32 || invalid.Contains(c) ? '-' : c);
        return sb.ToString().Trim().TrimEnd('.');
    }
}
