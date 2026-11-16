using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="JobStorageLocationResolver"/>.</summary>
public class JobStorageLocationResolverTests
{
    private const string Fallback = "/fallback/docs";

    [Fact]
    public void UserLocation_WithSharedLocation_MirrorsToShared()
    {
        var loc = JobStorageLocationResolver.Resolve("/shared", new UserProfile { CustomJobSaveLocation = "/server/me" }, Fallback);
        Assert.Equal("/server/me", loc.PrimaryRoot);
        Assert.Equal("/shared", loc.SecondaryRoot);
        Assert.True(loc.IsUserLocation);
    }

    [Fact]
    public void UserLocation_WithoutSharedLocation_DoesNotMirrorToFallback()
    {
        var loc = JobStorageLocationResolver.Resolve("  ", new UserProfile { CustomJobSaveLocation = "/server/me" }, Fallback);
        Assert.Equal("/server/me", loc.PrimaryRoot);
        Assert.Null(loc.SecondaryRoot);
    }

    [Fact]
    public void UserLocation_SameAsShared_DoesNotMirror()
    {
        var loc = JobStorageLocationResolver.Resolve("/shared/", new UserProfile { CustomJobSaveLocation = "/SHARED" }, Fallback);
        Assert.Null(loc.SecondaryRoot);
    }

    [Fact]
    public void NoUserLocation_UsesShared_ThenFallback()
    {
        Assert.Equal("/shared", JobStorageLocationResolver.Resolve("/shared", new UserProfile(), Fallback).PrimaryRoot);
        var none = JobStorageLocationResolver.Resolve(null, null, Fallback);
        Assert.Equal(Fallback, none.PrimaryRoot);
        Assert.Null(none.SecondaryRoot);
        Assert.False(none.IsUserLocation);
    }
}

/// <summary>Tests for <see cref="JobFolderMigrationService"/>, using real temp folders.</summary>
public class JobFolderMigrationServiceTests : IDisposable
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _temp;
    private readonly string _oldRoot;
    private readonly string _newRoot;

    public JobFolderMigrationServiceTests()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"HtbJobMigrationTests_{Guid.NewGuid():N}")
            .Options;
        using (var ctx = new AppDbContext(_options))
            ctx.Database.EnsureCreated();

        _temp = Path.Combine(Path.GetTempPath(), $"htb-migrate-{Guid.NewGuid():N}");
        _oldRoot = Path.Combine(_temp, "local", "Documents");
        _newRoot = Path.Combine(_temp, "server", "jobs");
        Directory.CreateDirectory(_oldRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private AppDbContext CreateContext() => new(_options);

    private JobFolderMigrationService CreateService(Func<string, bool>? isLocal = null, bool searchOldRoot = true) =>
        new(CreateContext, isLocal ?? (_ => true), searchOldRoot ? new[] { _oldRoot } : null);

    /// <summary>Creates a user (with the new job location) owning one job; returns (userId, jobId, jobNumber).</summary>
    private (int UserId, int JobId, string JobNumber) SeedUserWithJob(string? jobLocation = null)
    {
        using var ctx = CreateContext();
        var user = new UserProfile { UserName = "pat", DefaultTemplateSaveLocation = "", CustomJobSaveLocation = jobLocation ?? _newRoot };
        ctx.UserProfiles.Add(user);
        ctx.SaveChanges();

        var number = $"J-{Guid.NewGuid():N}"[..10];
        var job = new Job { JobNumber = number, JobName = "Test", UserProfileId = user.Id };
        ctx.Jobs.Add(job);
        ctx.SaveChanges();
        return (user.Id, job.Id, number);
    }

    private string WriteFile(string root, string jobNumber, string relative, string content)
    {
        var path = Path.Combine(root, jobNumber, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Apply_MovesWholeJobFolder_AndRelinksAttachments()
    {
        var (userId, jobId, number) = SeedUserWithJob();
        WriteFile(_oldRoot, number, "Package.pdf", "package");
        WriteFile(_oldRoot, number, Path.Combine("Old versions", "2026-01-01", "Package.pdf"), "old");
        var attachment = WriteFile(_oldRoot, number, Path.Combine("Attachments", "email.eml"), "mail");
        using (var ctx = CreateContext())
        {
            ctx.JobAttachments.Add(new JobAttachment { JobId = jobId, FileName = "email.eml", StoredPath = attachment, FileType = "Email" });
            ctx.SaveChanges();
        }

        var service = CreateService();
        var plan = service.Preview(userId);
        var move = Assert.Single(plan.Moves);
        Assert.Equal(3, move.FileCount);

        var result = service.Apply(plan);

        Assert.Empty(result.Failures);
        Assert.Equal(1, result.JobsMoved);
        Assert.Equal(3, result.FilesMoved);
        Assert.Equal(1, result.AttachmentsRelinked);
        Assert.False(Directory.Exists(Path.Combine(_oldRoot, number)));
        Assert.Equal("package", File.ReadAllText(Path.Combine(_newRoot, number, "Package.pdf")));
        Assert.True(File.Exists(Path.Combine(_newRoot, number, "Old versions", "2026-01-01", "Package.pdf")));

        using var check = CreateContext();
        var stored = check.JobAttachments.Single(a => a.JobId == jobId).StoredPath;
        Assert.Equal(Path.GetFullPath(Path.Combine(_newRoot, number, "Attachments", "email.eml")), stored);
        Assert.True(File.Exists(stored));
    }

    [Fact]
    public void Apply_NeverOverwrites_AndDropsIdenticalDuplicates()
    {
        var (userId, _, number) = SeedUserWithJob();
        WriteFile(_oldRoot, number, "same.pdf", "identical");
        WriteFile(_oldRoot, number, "Package.pdf", "from C drive");
        WriteFile(_newRoot, number, "same.pdf", "identical");
        WriteFile(_newRoot, number, "Package.pdf", "already on server");

        var service = CreateService();
        var plan = service.Preview(userId);
        Assert.True(Assert.Single(plan.Moves).DestinationExists);

        var result = service.Apply(plan);

        Assert.Empty(result.Failures);
        Assert.Equal(1, result.DuplicatesRemoved);
        Assert.Equal(1, result.FilesRenamed);
        var dest = Path.Combine(_newRoot, number);
        Assert.Equal("already on server", File.ReadAllText(Path.Combine(dest, "Package.pdf")));
        Assert.Equal("from C drive", File.ReadAllText(Path.Combine(dest, $"Package ({JobFolderMigrationService.ConflictSuffix}).pdf")));
        Assert.Equal("identical", File.ReadAllText(Path.Combine(dest, "same.pdf")));
        Assert.False(Directory.Exists(Path.Combine(_oldRoot, number)));
    }

    [Fact]
    public void Preview_IgnoresOtherUsersJobs()
    {
        var (userId, _, _) = SeedUserWithJob();
        string otherNumber;
        using (var ctx = CreateContext())
        {
            var other = new UserProfile { UserName = "sam", DefaultTemplateSaveLocation = "" };
            ctx.UserProfiles.Add(other);
            ctx.SaveChanges();
            otherNumber = $"K-{Guid.NewGuid():N}"[..10];
            ctx.Jobs.Add(new Job { JobNumber = otherNumber, JobName = "Theirs", UserProfileId = other.Id });
            ctx.SaveChanges();
        }
        WriteFile(_oldRoot, otherNumber, "Package.pdf", "not mine");

        var plan = CreateService().Preview(userId);

        Assert.False(plan.HasChanges);
        Assert.True(File.Exists(Path.Combine(_oldRoot, otherNumber, "Package.pdf")));
    }

    [Fact]
    public void Preview_SkipsFoldersThatAreNotLocal()
    {
        var (userId, _, number) = SeedUserWithJob();
        WriteFile(_oldRoot, number, "Package.pdf", "on a network share");

        var plan = CreateService(isLocal: path => !path.StartsWith(_oldRoot, StringComparison.OrdinalIgnoreCase)).Preview(userId);

        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void Preview_FindsOldRootFromAttachmentPaths()
    {
        var (userId, jobId, number) = SeedUserWithJob();
        var elsewhere = Path.Combine(_temp, "local", "OldCustomFolder");
        var attachment = WriteFile(elsewhere, number, Path.Combine("Attachments", "a.pdf"), "a");
        using (var ctx = CreateContext())
        {
            ctx.JobAttachments.Add(new JobAttachment { JobId = jobId, FileName = "a.pdf", StoredPath = attachment, FileType = "PDF" });
            ctx.SaveChanges();
        }

        var plan = CreateService(searchOldRoot: false).Preview(userId);

        Assert.Equal(Path.Combine(elsewhere, number), Assert.Single(plan.Moves).SourceDir);
    }

    [Fact]
    public void Preview_FromFolder_SearchesOnlyThatFolder_EvenWhenNotLocal()
    {
        var (userId, _, number) = SeedUserWithJob();
        var oldServerFolder = Path.Combine(_temp, "server", "old-pat");
        WriteFile(oldServerFolder, number, "Package.pdf", "old location");
        WriteFile(_oldRoot, number, "Package.pdf", "local copy, not part of this move");

        // Nothing counts as local: the old folder is a share, and must still be searched.
        var service = CreateService(isLocal: _ => false);
        var plan = service.Preview(userId, fromFolder: oldServerFolder);

        Assert.Equal(oldServerFolder, plan.FromFolder);
        Assert.Equal(Path.Combine(oldServerFolder, number), Assert.Single(plan.Moves).SourceDir);

        var result = service.Apply(plan);
        Assert.Equal(1, result.JobsMoved);
        Assert.Equal("old location", File.ReadAllText(Path.Combine(_newRoot, number, "Package.pdf")));
        Assert.True(File.Exists(Path.Combine(_oldRoot, number, "Package.pdf")));
    }

    [Fact]
    public void Preview_FromFolderSameAsDestination_FindsNothing()
    {
        var (userId, _, number) = SeedUserWithJob();
        WriteFile(_newRoot, number, "Package.pdf", "already there");

        var plan = CreateService().Preview(userId, fromFolder: _newRoot + Path.DirectorySeparatorChar);

        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void Preview_RequiresJobSaveLocation()
    {
        var (userId, _, _) = SeedUserWithJob(jobLocation: "");
        Assert.Throws<InvalidOperationException>(() => CreateService().Preview(userId));
    }
}
