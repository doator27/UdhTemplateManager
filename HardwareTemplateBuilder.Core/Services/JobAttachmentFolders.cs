using System;
using System.IO;
using System.Linq;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.Pdf;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Where a job's attachments live on disk, per release: the base list's files in
/// <c>{job folder}/Attachments/</c>, a release's in <c>{job folder}/Attachments/{ReleaseLabel}/</c>
/// (primary job save location, mirrored to the shared location when one is configured,
/// see <see cref="JobStorageLocationResolver"/>). Also moves an attachment to another release.
/// </summary>
public static class JobAttachmentFolders
{
    /// <summary>The attachments subfolder of a job folder.</summary>
    public const string AttachmentsFolderName = "Attachments";

    /// <summary>The attachments folder for a release under a job folder.</summary>
    /// <param name="jobFolder">The job's folder (<c>{root}/{JobNumber}</c>).</param>
    /// <param name="releaseLabel">The release label (null/blank = base list).</param>
    public static string Folder(string jobFolder, string? releaseLabel) =>
        string.IsNullOrWhiteSpace(releaseLabel)
            ? Path.Combine(jobFolder, AttachmentsFolderName)
            : Path.Combine(jobFolder, AttachmentsFolderName, JobReleaseNaming.SafeFileNamePart(releaseLabel.Trim()));

    /// <summary>The release's label, or null for the base list (or an unknown release).</summary>
    /// <param name="ctx">An open context.</param>
    /// <param name="releaseId">The release (null = base list).</param>
    public static string? ReleaseLabel(AppDbContext ctx, int? releaseId) =>
        releaseId == null ? null : ctx.JobReleases.Where(r => r.Id == releaseId).Select(r => r.ReleaseLabel).FirstOrDefault();

    /// <summary>The primary attachments folder for a job's release (not created).</summary>
    /// <param name="ctx">An open context.</param>
    /// <param name="job">The job.</param>
    /// <param name="releaseId">The release (null = base list).</param>
    public static string PrimaryFolder(AppDbContext ctx, Job job, int? releaseId) =>
        Folder(Path.Combine(JobStorageLocationResolver.ResolveForJob(ctx, job).PrimaryRoot, job.JobNumber), ReleaseLabel(ctx, releaseId));

    /// <summary>The mirrored (shared location) attachments folder, or null when there's no mirror.</summary>
    /// <param name="ctx">An open context.</param>
    /// <param name="job">The job.</param>
    /// <param name="releaseId">The release (null = base list).</param>
    public static string? SecondaryFolder(AppDbContext ctx, Job job, int? releaseId)
    {
        var root = JobStorageLocationResolver.ResolveForJob(ctx, job).SecondaryRoot;
        return root == null ? null : Folder(Path.Combine(root, job.JobNumber), ReleaseLabel(ctx, releaseId));
    }

    /// <summary>A path in <paramref name="folder"/> for <paramref name="fileName"/> that doesn't exist yet ("name (1).ext", ...).</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="fileName">The wanted file name.</param>
    public static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        int n = 1;
        while (File.Exists(path))
            path = Path.Combine(folder, $"{stem} ({n++}){ext}");
        return path;
    }

    /// <summary>
    /// Moves an attachment to another release of its job: the record's <see cref="JobAttachment.ReleaseId"/>,
    /// and the file into that release's attachments folder (never overwriting; the mirrored copy moves too).
    /// A missing file only changes the record.
    /// </summary>
    /// <param name="ctx">An open context.</param>
    /// <param name="attachmentId">The attachment.</param>
    /// <param name="releaseId">The release to move it to (null = base list).</param>
    /// <returns>The updated attachment, or null when it doesn't exist.</returns>
    public static JobAttachment? MoveToRelease(AppDbContext ctx, int attachmentId, int? releaseId)
    {
        var a = ctx.JobAttachments.FirstOrDefault(x => x.Id == attachmentId);
        if (a == null) return null;
        if (a.ReleaseId == releaseId) return a;
        var job = ctx.Jobs.FirstOrDefault(j => j.Id == a.JobId);
        if (job == null) return a;

        var oldSecondary = SecondaryFolder(ctx, job, a.ReleaseId);
        if (File.Exists(a.StoredPath))
        {
            var folder = PrimaryFolder(ctx, job, releaseId);
            Directory.CreateDirectory(folder);
            var dest = UniquePath(folder, Path.GetFileName(a.StoredPath));
            File.Move(a.StoredPath, dest);
            if (oldSecondary != null) DirectoryMirrorHelper.DeleteFile(a.FileName, oldSecondary);
            var newSecondary = SecondaryFolder(ctx, job, releaseId);
            if (newSecondary != null) DirectoryMirrorHelper.CopyFile(dest, newSecondary);
            a.StoredPath = dest;
            a.FileName = Path.GetFileName(dest);
        }
        a.ReleaseId = releaseId;
        ctx.SaveChanges();
        return a;
    }
}
