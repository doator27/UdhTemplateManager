using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Each job release has its own bulk add draft.</summary>
public class BulkAddDraftRepositoryTests
{
    [Fact]
    public void Drafts_AreKeptPerRelease()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"HtbDraftTests_{Guid.NewGuid():N}")
            .Options;
        int jobId;
        using (var ctx = new AppDbContext(options))
        {
            var job = new Job { JobNumber = "24117", JobName = "Test" };
            ctx.Jobs.Add(job);
            ctx.SaveChanges();
            jobId = job.Id;
        }

        using (var ctx = new AppDbContext(options))
        {
            var repo = new BulkAddDraftRepository(ctx);
            repo.Upsert(jobId, "base");
            repo.Upsert(jobId, "addendum", releaseId: 7);
            repo.Upsert(jobId, "addendum 2", releaseId: 7);
        }

        using (var ctx = new AppDbContext(options))
        {
            var repo = new BulkAddDraftRepository(ctx);
            Assert.Equal("base", repo.GetByJob(jobId)!.DraftJson);
            Assert.Equal("addendum 2", repo.GetByJob(jobId, 7)!.DraftJson);
            Assert.Null(repo.GetByJob(jobId, 8));
            Assert.Equal(2, ctx.BulkAddDrafts.Count());

            repo.DeleteByJob(jobId, 7);
            Assert.Null(repo.GetByJob(jobId, 7));
            Assert.NotNull(repo.GetByJob(jobId));
        }
    }
}
