using System.Net.Http;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.Pdf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Pdf;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="JobPackageGenerationService"/>.</summary>
public class JobPackageGenerationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public JobPackageGenerationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"HtbJobPkgTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // A shared, kept-open connection lets every context created via the service's
        // Func<AppDbContext> factory see the same in-memory database.
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

    private static JobPackageGenerationService MakeService(Func<AppDbContext> factory) =>
        new(factory, new HttpClient());

    /// <summary>
    /// Seeds a job with the given hardware items (each with one working, real-PDF template
    /// unless <paramref name="brokenTemplate"/> is set, in which case the single item's
    /// template has no local/online link). Also seeds the <c>TemplateStorageLocation</c>
    /// app setting to point at the test's temp directory.
    /// </summary>
    private int SeedJob(string[] modelNumbers, bool brokenTemplate = false)
    {
        using var ctx = CreateContext();

        var mfr = ctx.Manufacturers.Add(new Manufacturer { ManufacturerName = "Acme" }).Entity;
        var desc = ctx.Descriptions.Add(new Description { DescriptionText = "Hardware", SortOrder = 1 }).Entity;
        var customer = ctx.Customers.Add(new Customer { CustomerName = "Cust" }).Entity;
        var pm = ctx.ProjectManagers.Add(new ProjectManager { ProjectManagerName = "PM" }).Entity;
        var profile = ctx.UserProfiles.Add(new UserProfile { UserName = "Tester" }).Entity;
        ctx.AppSettings.Single(s => s.Key == "TemplateStorageLocation").Value = _tempDir;
        ctx.SaveChanges();

        var job = ctx.Jobs.Add(new Job
        {
            JobNumber        = $"J-{Guid.NewGuid():N}"[..8],
            JobName          = "Test Job",
            CustomerId       = customer.Id,
            ProjectManagerId = pm.Id,
            UserProfileId    = profile.Id
        }).Entity;
        ctx.SaveChanges();

        foreach (var modelNumber in modelNumbers)
        {
            var item = ctx.HardwareItems.Add(new HardwareItem
            {
                ManufacturerId = mfr.Id,
                DescriptionId  = desc.Id,
                ModelNumber    = modelNumber
            }).Entity;
            ctx.SaveChanges();

            var template = new IndividualTemplate
            {
                ManufacturerId = mfr.Id,
                DescriptionId  = desc.Id,
                DoorMaterialId = 1,
                TemplateNumber = $"T-{modelNumber}",
                PagesToPrint   = "1",
                LocalLink      = brokenTemplate ? null : CreateSamplePdf($"T-{modelNumber}")
            };
            ctx.IndividualTemplates.Add(template);
            ctx.SaveChanges();

            ctx.HardwareItemTemplates.Add(new HardwareItemTemplate
            {
                HardwareItemId       = item.Id,
                IndividualTemplateId = template.Id
            });
            ctx.JobHardware.Add(new JobHardware { JobId = job.Id, HardwareItemId = item.Id });
        }
        ctx.SaveChanges();

        return job.Id;
    }

    // ---------- Tests ----------

    [Fact]
    public async Task GenerateAsync_HappyPath_WritesSnapshotsIncrementsFrequencyAndAppendsHistory()
    {
        var jobId = SeedJob(new[] { "MODEL-1" });

        var result = await MakeService(CreateContext).GenerateAsync(jobId);

        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(1, result.TemplateCount);

        using var ctx = CreateContext();
        Assert.Equal(1, ctx.JobTemplateSnapshots.Count(s => s.JobId == jobId));

        var item = ctx.HardwareItems.Single(h => h.ModelNumber == "MODEL-1");
        Assert.Equal(1, item.Frequency);

        var job = ctx.Jobs.Find(jobId)!;
        var historyPath = Path.Combine(_tempDir, job.JobNumber, "job_history.txt");
        Assert.True(File.Exists(historyPath));
    }

    [Fact]
    public async Task GenerateAsync_NullOrderedHardware_LoadsFromCanonicalDbOrder()
    {
        var jobId = SeedJob(new[] { "MODEL-A", "MODEL-B" });

        var result = await MakeService(CreateContext).GenerateAsync(jobId, orderedHardware: null);

        Assert.Equal(2, result.TemplateCount);
    }

    [Fact]
    public async Task GenerateAsync_ExplicitOrderedHardware_OverridesDbSet()
    {
        var jobId = SeedJob(new[] { "MODEL-A", "MODEL-B" });

        using var ctx = CreateContext();
        var onlyItem = ctx.HardwareItems.Single(h => h.ModelNumber == "MODEL-A");
        var overrideOrder = new List<(int, string?, string?)> { (onlyItem.Id, null, null) };

        var result = await MakeService(CreateContext).GenerateAsync(jobId, overrideOrder);

        Assert.Equal(1, result.TemplateCount);
    }

    [Fact]
    public async Task GenerateAsync_FailurePath_WritesNoPartialSnapshotsAndDoesNotIncrementFrequency()
    {
        var jobId = SeedJob(new[] { "MODEL-BROKEN" }, brokenTemplate: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MakeService(CreateContext).GenerateAsync(jobId));

        using var ctx = CreateContext();
        Assert.Equal(0, ctx.JobTemplateSnapshots.Count(s => s.JobId == jobId));

        var item = ctx.HardwareItems.Single(h => h.ModelNumber == "MODEL-BROKEN");
        Assert.Equal(0, item.Frequency);
    }

    [Fact]
    public async Task GenerateAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var jobId = SeedJob(new[] { "MODEL-1" });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => MakeService(CreateContext).GenerateAsync(jobId, cancellationToken: cts.Token));
    }
}
