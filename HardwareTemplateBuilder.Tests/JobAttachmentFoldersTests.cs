using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for attachments tied to job releases (<see cref="JobAttachmentFolders"/>).</summary>
public class JobAttachmentFoldersTests : IDisposable
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _root;

    public JobAttachmentFoldersTests()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"HtbAttachmentTests_{Guid.NewGuid():N}")
            .Options;
        _root = Path.Combine(Path.GetTempPath(), $"htb-attach-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void FolderPerRelease()
    {
        var job = Path.Combine("x", "24117");
        Assert.Equal(Path.Combine(job, "Attachments"), JobAttachmentFolders.Folder(job, null));
        Assert.Equal(Path.Combine(job, "Attachments", "Addendum 1"), JobAttachmentFolders.Folder(job, " Addendum 1 "));
        Assert.Equal(Path.Combine(job, "Attachments", "Rev A-B"), JobAttachmentFolders.Folder(job, "Rev A/B"));
    }

    [Fact]
    public void ListsByRelease_AndMovesBetweenReleases()
    {
        using var ctx = new AppDbContext(_options);
        var user = new UserProfile { UserName = "pat", DefaultTemplateSaveLocation = "", CustomJobSaveLocation = _root };
        ctx.UserProfiles.Add(user);
        ctx.SaveChanges();
        var job = new Job { JobNumber = "24117", JobName = "Test", UserProfileId = user.Id };
        ctx.Jobs.Add(job);
        ctx.SaveChanges();
        var release = new JobRelease { JobId = job.Id, ReleaseNumber = 1, ReleaseLabel = "Addendum 1" };
        ctx.JobReleases.Add(release);
        ctx.SaveChanges();

        var baseFolder = JobAttachmentFolders.PrimaryFolder(ctx, job, null);
        Directory.CreateDirectory(baseFolder);
        var file = Path.Combine(baseFolder, "spec.pdf");
        File.WriteAllText(file, "pdf");
        var a = new JobAttachment { JobId = job.Id, FileName = "spec.pdf", StoredPath = file, FileType = "PDF" };
        ctx.JobAttachments.Add(a);
        ctx.JobAttachments.Add(new JobAttachment { JobId = job.Id, ReleaseId = release.Id, FileName = "add1.pdf", StoredPath = "add1.pdf", FileType = "PDF" });
        ctx.SaveChanges();

        var repo = new JobAttachmentRepository(ctx);
        Assert.Equal(new[] { "spec.pdf" }, repo.GetByRelease(job.Id, null).Select(x => x.FileName));
        Assert.Equal(new[] { "add1.pdf" }, repo.GetByRelease(job.Id, release.Id).Select(x => x.FileName));

        // A same-named file already in the release folder isn't overwritten.
        var releaseFolder = Path.Combine(_root, "24117", "Attachments", "Addendum 1");
        Directory.CreateDirectory(releaseFolder);
        File.WriteAllText(Path.Combine(releaseFolder, "spec.pdf"), "other");

        var moved = JobAttachmentFolders.MoveToRelease(ctx, a.Id, release.Id)!;
        Assert.Equal(release.Id, moved.ReleaseId);
        Assert.Equal(Path.Combine(releaseFolder, "spec (1).pdf"), moved.StoredPath);
        Assert.Equal("spec (1).pdf", moved.FileName);
        Assert.False(File.Exists(file));
        Assert.Equal("pdf", File.ReadAllText(moved.StoredPath));
        Assert.Empty(repo.GetByRelease(job.Id, null));
        Assert.Equal(2, repo.GetByRelease(job.Id, release.Id).Count());
    }
}
