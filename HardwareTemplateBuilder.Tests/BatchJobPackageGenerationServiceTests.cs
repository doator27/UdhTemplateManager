using System.Net.Http;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.Pdf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Pdf;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="BatchJobPackageGenerationService"/>.</summary>
public class BatchJobPackageGenerationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public BatchJobPackageGenerationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"HtbBatchTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ---------- Helpers ----------

    private AppDbContext CreateContext() => new(_options);

    private string CreateSamplePdf(string name)
    {
        var path = Path.Combine(_tempDir, $"{name}.pdf");
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.Save(path);
        return path;
    }

    private static BatchJobPackageGenerationService MakeService(Func<AppDbContext> factory) =>
        new(factory, new HttpClient());

    /// <summary>Seeds a single job with one hardware item and template, working or broken.</summary>
    private int SeedJob(string jobNumber, bool brokenTemplate = false)
    {
        using var ctx = CreateContext();

        var mfr = ctx.Manufacturers.FirstOrDefault(m => m.ManufacturerName == "Acme")
                  ?? ctx.Manufacturers.Add(new Manufacturer { ManufacturerName = "Acme" }).Entity;
        var desc = ctx.Descriptions.FirstOrDefault()
                   ?? ctx.Descriptions.Add(new Description { DescriptionText = "Hardware", SortOrder = 1 }).Entity;
        var customer = ctx.Customers.FirstOrDefault()
                       ?? ctx.Customers.Add(new Customer { CustomerName = "Cust" }).Entity;
        var pm = ctx.ProjectManagers.FirstOrDefault()
                 ?? ctx.ProjectManagers.Add(new ProjectManager { ProjectManagerName = "PM" }).Entity;
        var profile = ctx.UserProfiles.FirstOrDefault()
                      ?? ctx.UserProfiles.Add(new UserProfile { UserName = "Tester" }).Entity;
        ctx.SaveChanges();

        var setting = ctx.AppSettings.Single(s => s.Key == "TemplateStorageLocation");
        setting.Value = _tempDir;
        ctx.SaveChanges();

        var job = ctx.Jobs.Add(new Job
        {
            JobNumber        = jobNumber,
            JobName          = $"Job {jobNumber}",
            CustomerId       = customer.Id,
            ProjectManagerId = pm.Id,
            UserProfileId    = profile.Id
        }).Entity;
        ctx.SaveChanges();

        var item = ctx.HardwareItems.Add(new HardwareItem
        {
            ManufacturerId = mfr.Id,
            DescriptionId  = desc.Id,
            ModelNumber    = $"MODEL-{jobNumber}"
        }).Entity;
        ctx.SaveChanges();

        var template = new IndividualTemplate
        {
            ManufacturerId = mfr.Id,
            DescriptionId  = desc.Id,
            DoorMaterialId = 1,
            TemplateNumber = $"T-{jobNumber}",
            PagesToPrint   = "1",
            LocalLink      = brokenTemplate ? null : CreateSamplePdf($"T-{jobNumber}")
        };
        ctx.IndividualTemplates.Add(template);
        ctx.SaveChanges();

        ctx.HardwareItemTemplates.Add(new HardwareItemTemplate
        {
            HardwareItemId       = item.Id,
            IndividualTemplateId = template.Id
        });
        ctx.JobHardware.Add(new JobHardware { JobId = job.Id, HardwareItemId = item.Id });
        ctx.SaveChanges();

        return job.Id;
    }

    // ---------- Tests ----------

    [Fact]
    public async Task RunBatchAsync_OneJobFailsAmongThree_OthersStillSucceed()
    {
        var okId1 = SeedJob("J-OK-1");
        var brokenId = SeedJob("J-BROKEN", brokenTemplate: true);
        var okId2 = SeedJob("J-OK-2");

        var result = await MakeService(CreateContext)
            .RunBatchAsync(new[] { okId1, brokenId, okId2 });

        Assert.Equal(3, result.TotalJobs);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(3, result.Jobs.Count);

        var failedJob = result.Jobs.Single(j => j.JobId == brokenId);
        Assert.False(failedJob.Success);
        Assert.False(string.IsNullOrWhiteSpace(failedJob.ErrorMessage));

        Assert.True(result.Jobs.Single(j => j.JobId == okId1).Success);
        Assert.True(result.Jobs.Single(j => j.JobId == okId2).Success);
    }

    [Fact]
    public async Task RunBatchAsync_CancelledMidRun_StopsRemainingJobs()
    {
        var id1 = SeedJob("J-1");
        var id2 = SeedJob("J-2");
        var id3 = SeedJob("J-3");

        using var cts = new CancellationTokenSource();
        var seenJobIds = new List<int>();

        var progress = new SyncProgress<BatchGenerationProgress>(p =>
        {
            if (p.CompletedJob is { } job)
            {
                seenJobIds.Add(job.JobId);
                if (seenJobIds.Count == 1) cts.Cancel();
            }
        });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => MakeService(CreateContext).RunBatchAsync(new[] { id1, id2, id3 }, progress, cts.Token));

        // Only the first job's completion should have been reported before cancellation took effect.
        Assert.Single(seenJobIds);
    }

    [Fact]
    public async Task RunBatchAsync_ReportsExactlyOneCompletedJobEventPerJobInOrder()
    {
        var id1 = SeedJob("J-A");
        var id2 = SeedJob("J-B");

        var completedIds = new List<int>();
        var progress = new SyncProgress<BatchGenerationProgress>(p =>
        {
            if (p.CompletedJob is { } job) completedIds.Add(job.JobId);
        });

        await MakeService(CreateContext).RunBatchAsync(new[] { id1, id2 }, progress);

        Assert.Equal(new[] { id1, id2 }, completedIds);
    }
}

/// <summary>
/// A synchronous <see cref="IProgress{T}"/> implementation that invokes the callback inline,
/// giving tests deterministic ordering without needing a UI dispatcher.
/// </summary>
file sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _callback;
    public SyncProgress(Action<T> callback) => _callback = callback;
    public void Report(T value) => _callback(value);
}
