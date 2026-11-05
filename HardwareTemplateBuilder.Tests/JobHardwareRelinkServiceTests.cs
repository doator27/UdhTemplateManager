using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="JobHardwareRelinkService"/>.</summary>
public class JobHardwareRelinkServiceTests : IDisposable
{
    private readonly DbContextOptions<AppDbContext> _options;

    public JobHardwareRelinkServiceTests()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"HtbRelinkTests_{Guid.NewGuid():N}")
            .Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() { }

    private AppDbContext CreateContext() => new(_options);

    private int SeedJobWithHardware(out int hardwareItemId)
    {
        using var ctx = CreateContext();

        var manufacturer = new Manufacturer { ManufacturerName = "Acme" };
        var description = new Description { DescriptionText = "Hinge" };
        ctx.Manufacturers.Add(manufacturer);
        ctx.Descriptions.Add(description);
        ctx.SaveChanges();

        var hw = new HardwareItem
        {
            ManufacturerId = manufacturer.Id,
            DescriptionId = description.Id,
            ModelNumber = "H-100"
        };
        ctx.HardwareItems.Add(hw);

        var job = new Job { JobNumber = "J-1", JobName = "Test Job" };
        ctx.Jobs.Add(job);
        ctx.SaveChanges();

        ctx.JobHardware.Add(new JobHardware
        {
            JobId = job.Id,
            HardwareItemId = hw.Id,
            CustomDescription = "Custom A",
            Remarks = "Some remark"
        });
        ctx.SaveChanges();

        hardwareItemId = hw.Id;
        return job.Id;
    }

    [Fact]
    public void Relink_RecreatesHardwareLinks_AndCreatesDraftWhenMissing()
    {
        var jobId = SeedJobWithHardware(out var hardwareItemId);

        var service = new JobHardwareRelinkService();
        using (var ctx = CreateContext())
        {
            var result = service.Relink(ctx, jobId);

            Assert.True(result.Success);
            Assert.Equal(1, result.RelinkedCount);
            Assert.True(result.DraftCreated);
        }

        using (var ctx = CreateContext())
        {
            var rows = ctx.JobHardware.Where(jh => jh.JobId == jobId).ToList();
            Assert.Single(rows);
            Assert.Equal(hardwareItemId, rows[0].HardwareItemId);
            Assert.Equal("Custom A", rows[0].CustomDescription);
            Assert.Equal("Some remark", rows[0].Remarks);

            var draft = new BulkAddDraftRepository(ctx).GetByJob(jobId);
            Assert.NotNull(draft);
        }
    }

    [Fact]
    public void Relink_DoesNotCreateDraft_WhenOneAlreadyExists()
    {
        var jobId = SeedJobWithHardware(out _);

        using (var ctx = CreateContext())
        {
            new BulkAddDraftRepository(ctx).Upsert(jobId, "{\"version\":2,\"manufacturerIds\":[],\"items\":[]}");
        }

        var service = new JobHardwareRelinkService();
        using var relinkCtx = CreateContext();
        var result = service.Relink(relinkCtx, jobId);

        Assert.True(result.Success);
        Assert.False(result.DraftCreated);
    }

    [Fact]
    public void Relink_ReturnsFailure_WhenJobHasNoHardware()
    {
        using var ctx = CreateContext();
        var job = new Job { JobNumber = "J-2", JobName = "Empty Job" };
        ctx.Jobs.Add(job);
        ctx.SaveChanges();

        var service = new JobHardwareRelinkService();
        var result = service.Relink(ctx, job.Id);

        Assert.False(result.Success);
        Assert.Equal(0, result.RelinkedCount);
    }

    [Fact]
    public void Relink_RepointsToSiblingHardwareItem_WhenCurrentItemHasNoTemplateLinks()
    {
        int jobId, orphanItemId, goodItemId, templateId;
        using (var ctx = CreateContext())
        {
            var manufacturer = new Manufacturer { ManufacturerName = "Acme" };
            var description = new Description { DescriptionText = "Hinge" };
            ctx.Manufacturers.Add(manufacturer);
            ctx.Descriptions.Add(description);
            ctx.SaveChanges();

            // Simulates a post-migration duplicate: two HardwareItem rows with the same
            // manufacturer/description/model number, only one of which has template links.
            var orphan = new HardwareItem { ManufacturerId = manufacturer.Id, DescriptionId = description.Id, ModelNumber = "H-100" };
            var good = new HardwareItem { ManufacturerId = manufacturer.Id, DescriptionId = description.Id, ModelNumber = "H-100" };
            ctx.HardwareItems.AddRange(orphan, good);
            ctx.SaveChanges();

            var template = new IndividualTemplate { ManufacturerId = manufacturer.Id, DescriptionId = description.Id, TemplateNumber = "T-1" };
            ctx.IndividualTemplates.Add(template);
            ctx.SaveChanges();

            ctx.HardwareItemTemplates.Add(new HardwareItemTemplate { HardwareItemId = good.Id, IndividualTemplateId = template.Id, JobId = null });

            var job = new Job { JobNumber = "J-3", JobName = "Orphaned Job" };
            ctx.Jobs.Add(job);
            ctx.SaveChanges();

            ctx.JobHardware.Add(new JobHardware { JobId = job.Id, HardwareItemId = orphan.Id });
            ctx.SaveChanges();

            jobId = job.Id;
            orphanItemId = orphan.Id;
            goodItemId = good.Id;
            templateId = template.Id;
        }

        var service = new JobHardwareRelinkService();
        using (var ctx = CreateContext())
        {
            var result = service.Relink(ctx, jobId);

            Assert.True(result.Success);
            Assert.Equal(1, result.RepointedCount);
            Assert.Equal(0, result.StillUnlinkedCount);
        }

        using (var ctx = CreateContext())
        {
            var row = ctx.JobHardware.Single(jh => jh.JobId == jobId);
            Assert.Equal(goodItemId, row.HardwareItemId);
            Assert.NotEqual(orphanItemId, row.HardwareItemId);
        }
    }
}
