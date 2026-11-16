using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>One job folder found outside the user's job save location, and where it will go.</summary>
public class JobFolderMove
{
    /// <summary>Gets the job's database ID.</summary>
    public int JobId { get; init; }

    /// <summary>Gets the job number (also the folder name).</summary>
    public string JobNumber { get; init; } = string.Empty;

    /// <summary>Gets the existing job folder, e.g. <c>C:\Users\me\Documents\12345</c>.</summary>
    public string SourceDir { get; init; } = string.Empty;

    /// <summary>Gets the job folder it will be merged into under the destination root.</summary>
    public string DestinationDir { get; init; } = string.Empty;

    /// <summary>Gets the number of files currently in <see cref="SourceDir"/> (all subfolders).</summary>
    public int FileCount { get; init; }

    /// <summary>Gets whether <see cref="DestinationDir"/> already exists, so the folders will be merged.</summary>
    public bool DestinationExists { get; init; }

    /// <summary>Gets a one-line description for previews.</summary>
    public string Summary =>
        $"{JobNumber}: {FileCount} file(s) from {SourceDir}" + (DestinationExists ? "  (merging into existing folder)" : string.Empty);
}

/// <summary>The dry-run result of <see cref="JobFolderMigrationService.Preview"/>.</summary>
public class JobFolderMigrationPlan
{
    /// <summary>Gets the user profile whose jobs are being moved.</summary>
    public int UserProfileId { get; init; }

    /// <summary>Gets the user's job save location that folders will be moved into.</summary>
    public string DestinationRoot { get; init; } = string.Empty;

    /// <summary>Gets the local folders that were searched for job folders.</summary>
    public List<string> SearchedRoots { get; init; } = new();

    /// <summary>Gets the job folders that will be moved.</summary>
    public List<JobFolderMove> Moves { get; init; } = new();

    /// <summary>Gets whether there is anything to move.</summary>
    public bool HasChanges => Moves.Count > 0;
}

/// <summary>The outcome of <see cref="JobFolderMigrationService.Apply"/>.</summary>
public class JobFolderMigrationResult
{
    /// <summary>Gets or sets how many job folders were fully moved (source folder removed).</summary>
    public int JobsMoved { get; set; }

    /// <summary>Gets or sets how many files were moved.</summary>
    public int FilesMoved { get; set; }

    /// <summary>
    /// Gets or sets how many files already existed at the destination with different content and
    /// were therefore moved under a new name (nothing is ever overwritten).
    /// </summary>
    public int FilesRenamed { get; set; }

    /// <summary>Gets or sets how many source files were identical copies of a destination file and were removed.</summary>
    public int DuplicatesRemoved { get; set; }

    /// <summary>Gets or sets how many job attachment records were re-pointed at their moved files.</summary>
    public int AttachmentsRelinked { get; set; }

    /// <summary>Gets the per-file/per-job problems; files involved are left where they were.</summary>
    public List<string> Failures { get; } = new();
}

/// <summary>
/// Moves a user's job folders that were saved on this machine's local drives (typically
/// My Documents, before the user had a job save location) into the folder set in their
/// <see cref="UserProfile.CustomJobSaveLocation"/>, e.g. a folder on the server.
/// <para>
/// Only jobs owned by the profile are moved, only from local fixed drives (never from network
/// shares, so other users' copies are untouched), and nothing is ever overwritten: a file that
/// already exists at the destination with different content is moved alongside it under a new
/// name. <see cref="JobAttachment.StoredPath"/> rows are updated to the new paths.
/// <see cref="JobTemplateSnapshot"/> rows are never modified (their path is historical only).
/// Failures are collected and reported; a file that couldn't be moved stays where it was.
/// </para>
/// </summary>
public class JobFolderMigrationService
{
    /// <summary>Suffix added to a moved file whose name is already taken at the destination.</summary>
    public const string ConflictSuffix = "moved from local drive";

    private readonly Func<AppDbContext> _contextFactory;
    private readonly Func<string, bool> _isLocalFolder;
    private readonly IReadOnlyList<string> _extraSearchRoots;

    /// <summary>Initializes the service.</summary>
    /// <param name="contextFactory">Creates a fresh database context per operation.</param>
    /// <param name="isLocalFolder">
    /// Decides whether a folder is on this machine's local drives. Defaults to
    /// <see cref="IsLocalFixedDriveFolder"/>; tests pass their own.
    /// </param>
    /// <param name="extraSearchRoots">
    /// Extra folders to search in addition to My Documents, the shared App Settings location and
    /// the folders the user's attachments point at. Mainly for tests.
    /// </param>
    public JobFolderMigrationService(
        Func<AppDbContext> contextFactory,
        Func<string, bool>? isLocalFolder = null,
        IEnumerable<string>? extraSearchRoots = null)
    {
        _contextFactory = contextFactory;
        _isLocalFolder = isLocalFolder ?? IsLocalFixedDriveFolder;
        _extraSearchRoots = extraSearchRoots?.ToList() ?? new List<string>();
    }

    /// <summary>
    /// Finds every job folder owned by <paramref name="userProfileId"/> that lives in a local
    /// folder other than the user's job save location. Reads only; changes nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The profile doesn't exist or has no job save location.</exception>
    public JobFolderMigrationPlan Preview(int userProfileId)
    {
        using var ctx = _contextFactory();
        var profile = ctx.UserProfiles.AsNoTracking().FirstOrDefault(u => u.Id == userProfileId)
            ?? throw new InvalidOperationException($"User profile {userProfileId} was not found.");

        var destination = profile.CustomJobSaveLocation?.Trim();
        if (string.IsNullOrEmpty(destination))
            throw new InvalidOperationException(
                $"{profile.UserName} has no job save location. Set one in Admin > User Profiles first.");

        var jobs = ctx.Jobs.AsNoTracking().Where(j => j.UserProfileId == userProfileId).ToList();
        var jobIds = jobs.Select(j => j.Id).ToList();
        var jobNumbers = jobs.Select(j => j.JobNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Where could this user's jobs have been written on this machine?
        var candidates = new List<string> { JobStorageLocationResolver.FallbackRoot };
        var shared = new AppSettingRepository(ctx).GetValue(JobStorageLocationResolver.SharedLocationSettingKey);
        if (!string.IsNullOrWhiteSpace(shared)) candidates.Add(shared.Trim());
        candidates.AddRange(_extraSearchRoots);

        var attachmentPaths = ctx.JobAttachments.AsNoTracking()
            .Where(a => jobIds.Contains(a.JobId)).Select(a => a.StoredPath).ToList();
        var snapshotPaths = ctx.JobTemplateSnapshots.AsNoTracking()
            .Where(s => jobIds.Contains(s.JobId)).Select(s => s.SnapshotLocalLink).ToList();
        candidates.AddRange(attachmentPaths.Select(p => RootAbove(p, jobNumbers, levels: 2)).OfType<string>());
        candidates.AddRange(snapshotPaths.Select(p => RootAbove(p, jobNumbers, levels: 1)).OfType<string>());

        var roots = candidates
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim().TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => !JobStorageLocationResolver.PathsEqual(r, destination) && !IsUnder(r, destination))
            .Where(r => SafeIsLocal(r) && Directory.Exists(r))
            .ToList();

        var moves = new List<JobFolderMove>();
        foreach (var job in jobs.OrderBy(j => j.JobNumber))
        {
            if (string.IsNullOrWhiteSpace(job.JobNumber)) continue;
            var destDir = Path.Combine(destination, job.JobNumber);

            foreach (var root in roots)
            {
                var sourceDir = Path.Combine(root, job.JobNumber);
                if (!Directory.Exists(sourceDir)) continue;

                var fileCount = SafeCountFiles(sourceDir);
                if (fileCount == 0) continue;

                moves.Add(new JobFolderMove
                {
                    JobId = job.Id,
                    JobNumber = job.JobNumber,
                    SourceDir = sourceDir,
                    DestinationDir = destDir,
                    FileCount = fileCount,
                    DestinationExists = Directory.Exists(destDir) || moves.Any(m => m.DestinationDir == destDir),
                });
            }
        }

        return new JobFolderMigrationPlan
        {
            UserProfileId = userProfileId,
            DestinationRoot = destination,
            SearchedRoots = roots,
            Moves = moves,
        };
    }

    /// <summary>
    /// Carries out a plan from <see cref="Preview"/>: moves every file, merges into existing
    /// destination folders without overwriting, removes emptied source folders, and re-points
    /// the jobs' attachment records. Per-file failures are collected, not thrown.
    /// </summary>
    public JobFolderMigrationResult Apply(JobFolderMigrationPlan plan)
    {
        var result = new JobFolderMigrationResult();

        foreach (var move in plan.Moves)
        {
            // Old full path -> new full path, for every file that actually moved.
            var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var failuresBefore = result.Failures.Count;

            try
            {
                Directory.CreateDirectory(move.DestinationDir);
                foreach (var file in Directory.GetFiles(move.SourceDir, "*", SearchOption.AllDirectories))
                    MoveOneFile(file, move, moved, result);

                RemoveEmptyFolders(move.SourceDir);
                if (!Directory.Exists(move.SourceDir) && result.Failures.Count == failuresBefore)
                    result.JobsMoved++;
            }
            catch (Exception ex)
            {
                result.Failures.Add($"{move.JobNumber}: {ex.Message}");
            }

            if (moved.Count > 0)
            {
                try { result.AttachmentsRelinked += RelinkAttachments(move.JobId, moved); }
                catch (Exception ex) { result.Failures.Add($"{move.JobNumber}: files moved, but attachment links could not be updated: {ex.Message}"); }
            }
        }

        return result;
    }

    /// <summary>The confirmation text shown before <see cref="Apply"/> runs.</summary>
    public static string DescribePlan(JobFolderMigrationPlan plan, string userName)
    {
        var lines = new List<string>
        {
            $"{plan.Moves.Count} job folder(s) for {userName} were found on this computer and will be moved to:",
            plan.DestinationRoot,
            string.Empty,
        };
        lines.AddRange(plan.Moves.Select(m => m.Summary));
        lines.Add(string.Empty);
        lines.Add("Files are moved, not copied: the originals are removed once each file is safely in the new folder. " +
                  "Nothing at the destination is overwritten; a file whose name is already taken there is kept under a new name. " +
                  "Job attachment links are updated to the new location.");
        lines.Add(string.Empty);
        lines.Add("Proceed?");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The message shown when <see cref="Preview"/> found nothing to move.</summary>
    public static string DescribeNothingToMove(JobFolderMigrationPlan plan, string userName) =>
        $"No job folders for {userName} were found on this computer's local drives outside {plan.DestinationRoot}." +
        Environment.NewLine + Environment.NewLine +
        "Searched:" + Environment.NewLine +
        (plan.SearchedRoots.Count > 0 ? string.Join(Environment.NewLine, plan.SearchedRoots) : "(no local folders to search)") +
        Environment.NewLine + Environment.NewLine +
        "Jobs saved on another computer have to be moved from that computer.";

    /// <summary>The summary shown after <see cref="Apply"/> finishes.</summary>
    public static string DescribeResult(JobFolderMigrationResult result, JobFolderMigrationPlan plan)
    {
        var lines = new List<string>
        {
            $"{result.JobsMoved} of {plan.Moves.Count} job folder(s) moved to {plan.DestinationRoot}.",
            $"{result.FilesMoved} file(s) moved, {result.DuplicatesRemoved} identical duplicate(s) removed, " +
            $"{result.FilesRenamed} file(s) kept under a new name because the name was taken, " +
            $"{result.AttachmentsRelinked} attachment link(s) updated.",
        };
        if (result.Failures.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"{result.Failures.Count} problem(s); these files were left where they were:");
            lines.AddRange(result.Failures);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static void MoveOneFile(string file, JobFolderMove move, Dictionary<string, string> moved, JobFolderMigrationResult result)
    {
        try
        {
            var relative = Path.GetRelativePath(move.SourceDir, file);
            var target = Path.Combine(move.DestinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (File.Exists(target))
            {
                if (FilesEqual(file, target))
                {
                    File.Delete(file);
                    moved[Path.GetFullPath(file)] = Path.GetFullPath(target);
                    result.DuplicatesRemoved++;
                    return;
                }

                target = UniqueConflictName(target);
                result.FilesRenamed++;
            }

            // Copy-then-delete works across drives (C: to a server share) and leaves the
            // original in place if anything goes wrong mid-copy.
            File.Copy(file, target, overwrite: false);
            if (new FileInfo(target).Length != new FileInfo(file).Length)
                throw new IOException("copied file size does not match the original");
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
            File.Delete(file);

            moved[Path.GetFullPath(file)] = Path.GetFullPath(target);
            result.FilesMoved++;
        }
        catch (Exception ex)
        {
            result.Failures.Add($"{move.JobNumber}: {file}: {ex.Message}");
        }
    }

    private int RelinkAttachments(int jobId, Dictionary<string, string> moved)
    {
        using var ctx = _contextFactory();
        var count = 0;
        foreach (var attachment in ctx.JobAttachments.Where(a => a.JobId == jobId).ToList())
        {
            if (string.IsNullOrWhiteSpace(attachment.StoredPath)) continue;
            string key;
            try { key = Path.GetFullPath(attachment.StoredPath); } catch { continue; }
            if (!moved.TryGetValue(key, out var newPath)) continue;
            attachment.StoredPath = newPath;
            count++;
        }
        ctx.SaveChanges();
        return count;
    }

    /// <summary>
    /// For a file stored under <c>{root}\{JobNumber}\...</c>, returns <c>{root}</c>.
    /// <paramref name="levels"/> is how many folders sit between the file and the job folder
    /// (1 = directly in the job folder, 2 = in a subfolder such as Attachments).
    /// </summary>
    private static string? RootAbove(string? filePath, HashSet<string> jobNumbers, int levels)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            for (var i = 1; i < levels && dir != null; i++)
                dir = Path.GetDirectoryName(dir);
            if (dir == null || !jobNumbers.Contains(Path.GetFileName(dir))) return null;
            return Path.GetDirectoryName(dir);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var p = path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var r = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return p.Replace('/', '\\').StartsWith(r.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    }

    private bool SafeIsLocal(string folder)
    {
        try { return _isLocalFolder(folder); } catch { return false; }
    }

    private static int SafeCountFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length; } catch { return 0; }
    }

    /// <summary>
    /// True for folders on this machine's fixed local drives (e.g. <c>C:\</c>). Network shares
    /// (<c>\\server\...</c>) and mapped network drives are not local, so they are never searched:
    /// those are shared locations other users may rely on.
    /// </summary>
    public static bool IsLocalFixedDriveFolder(string folder)
    {
        if (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal))
            return false;
        var root = Path.GetPathRoot(Path.GetFullPath(folder));
        if (string.IsNullOrEmpty(root)) return false;
        return new DriveInfo(root).DriveType == DriveType.Fixed;
    }

    private static bool FilesEqual(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;

        const int bufferSize = 81920;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        var bufA = new byte[bufferSize];
        var bufB = new byte[bufferSize];
        int readA;
        while ((readA = sa.Read(bufA, 0, bufferSize)) > 0)
        {
            var readB = 0;
            while (readB < readA)
            {
                var n = sb.Read(bufB, readB, readA - readB);
                if (n == 0) return false;
                readB += n;
            }
            if (!bufA.AsSpan(0, readA).SequenceEqual(bufB.AsSpan(0, readA))) return false;
        }
        return true;
    }

    private static string UniqueConflictName(string target)
    {
        var dir = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        var candidate = Path.Combine(dir, $"{stem} ({ConflictSuffix}){ext}");
        for (var n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(dir, $"{stem} ({ConflictSuffix} {n}){ext}");
        return candidate;
    }

    private static void RemoveEmptyFolders(string dir)
    {
        try
        {
            foreach (var sub in Directory.GetDirectories(dir))
                RemoveEmptyFolders(sub);
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch
        {
            // Leaving an empty folder behind is harmless.
        }
    }
}
