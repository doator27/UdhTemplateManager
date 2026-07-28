using System.Net.Http;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services;
using HardwareTemplateBuilder.Core.Services.Pdf;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Pdf;

namespace HardwareTemplateBuilder.Tests;

/// <summary>
/// Tests for <see cref="PdfAssemblyService.AssembleAsync"/>, focused on the categorized
/// failure report: a hardware item with no linked templates, a template that cannot be
/// acquired (attributed back to its owning hardware item), and the happy path.
/// </summary>
public class PdfAssemblyServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppDbContext _context;

    public PdfAssemblyServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"HtbAssemblyTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _context = new AppDbContext(options);
        _context.Database.OpenConnection();
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Database.CloseConnection();
        _context.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ---------- Helpers ----------

    private string CreateSamplePdf(string name)
    {
        var path = Path.Combine(_tempDir, $"{name}.pdf");
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.Save(path);
        return path;
    }

    private static PdfAssemblyService BuildService() => new(
        new TemplateSorter(new WeightTemplateSortStrategy()),
        new FileAcquirer(new HttpClient()),
        new PageRangeParser(),
        new PageExtractor(),
        new PageRotator(),
        new PdfMerger(),
        new CoverSheetBuilder(),
        new PageNumberer());

    /// <summary>Seeds a manufacturer/description pair shared across test hardware items.</summary>
    private (Manufacturer Mfr, Description Desc) SeedMfrAndDesc()
    {
        var mfr = _context.Manufacturers.Add(new Manufacturer { ManufacturerName = "Acme" }).Entity;
        var desc = _context.Descriptions.Add(new Description { DescriptionText = "Hardware", SortOrder = 1 }).Entity;
        _context.SaveChanges();
        return (mfr, desc);
    }

    /// <summary>
    /// Creates a <see cref="HardwareWithTemplates"/> whose single template is a real, acquirable
    /// local PDF, or (when <paramref name="withWorkingTemplate"/> is false) has no local/online
    /// link at all so acquisition fails.
    /// </summary>
    private HardwareWithTemplates SeedHardwareItem(
        Manufacturer mfr, Description desc, string modelNumber, string templateNumber,
        bool withWorkingTemplate)
    {
        var item = _context.HardwareItems.Add(new HardwareItem
        {
            ManufacturerId = mfr.Id,
            DescriptionId  = desc.Id,
            ModelNumber    = modelNumber
        }).Entity;
        _context.SaveChanges();

        var template = new IndividualTemplate
        {
            ManufacturerId = mfr.Id,
            DescriptionId  = desc.Id,
            DoorMaterialId = 1, // "Metal" is always seeded by AppDbContext.HasData
            TemplateNumber = templateNumber,
            PagesToPrint   = "1",
            LocalLink      = withWorkingTemplate ? CreateSamplePdf(templateNumber) : null,
            OnlineLink     = null,
            Manufacturer   = mfr
        };
        _context.IndividualTemplates.Add(template);
        _context.SaveChanges();

        return new HardwareWithTemplates
        {
            Item      = item,
            Templates = new List<IndividualTemplate> { template }.AsReadOnly()
        };
    }

    /// <summary>Creates a <see cref="HardwareWithTemplates"/> with no linked templates at all.</summary>
    private HardwareWithTemplates SeedHardwareItemWithNoTemplates(Manufacturer mfr, Description desc, string modelNumber)
    {
        var item = _context.HardwareItems.Add(new HardwareItem
        {
            ManufacturerId = mfr.Id,
            DescriptionId  = desc.Id,
            ModelNumber    = modelNumber
        }).Entity;
        _context.SaveChanges();

        return new HardwareWithTemplates
        {
            Item      = item,
            Templates = Array.Empty<IndividualTemplate>()
        };
    }

    private AssemblyRequest BuildRequest(IReadOnlyList<HardwareWithTemplates> hardware) => new()
    {
        Job             = new Job { JobNumber = "J-TEST", JobName = "Test Job" },
        Hardware        = hardware,
        OutputDirectory = _tempDir,
        AllDescriptions = _context.Descriptions.ToDictionary(d => d.Id),
        PreparedByName  = "Tester"
    };

    // ---------- Tests ----------

    [Fact]
    public async Task AssembleAsync_HardwareItemWithNoTemplates_ReportsHardwareItemCategoryFailure()
    {
        var (mfr, desc) = SeedMfrAndDesc();
        var working = SeedHardwareItem(mfr, desc, "MODEL-A", "T-A", withWorkingTemplate: true);
        var empty   = SeedHardwareItemWithNoTemplates(mfr, desc, "MODEL-B");

        var request = BuildRequest(new[] { working, empty });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildService().AssembleAsync(request));

        Assert.Contains("Hardware items with no templates linked (1)", ex.Message);
        Assert.Contains("MODEL-B", ex.Message);
        Assert.DoesNotContain("Templates that could not be processed", ex.Message);
    }

    [Fact]
    public async Task AssembleAsync_BadTemplate_ReportsTemplateCategoryFailureNamingOwner()
    {
        var (mfr, desc) = SeedMfrAndDesc();
        var working = SeedHardwareItem(mfr, desc, "MODEL-A", "T-A", withWorkingTemplate: true);
        var broken  = SeedHardwareItem(mfr, desc, "MODEL-BROKEN", "T-BROKEN", withWorkingTemplate: false);

        var request = BuildRequest(new[] { working, broken });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildService().AssembleAsync(request));

        Assert.Contains("Templates that could not be processed (1)", ex.Message);
        Assert.Contains("T-BROKEN", ex.Message);
        Assert.Contains("(used by: MODEL-BROKEN)", ex.Message);
        Assert.DoesNotContain("Hardware items with no templates linked", ex.Message);
    }

    [Fact]
    public async Task AssembleAsync_MixedFailures_GroupsUnderSeparateHeadersWithCorrectCounts()
    {
        var (mfr, desc) = SeedMfrAndDesc();
        var empty  = SeedHardwareItemWithNoTemplates(mfr, desc, "MODEL-EMPTY");
        var broken = SeedHardwareItem(mfr, desc, "MODEL-BROKEN", "T-BROKEN", withWorkingTemplate: false);

        var request = BuildRequest(new[] { empty, broken });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildService().AssembleAsync(request));

        Assert.Contains("2 problem(s) prevented package assembly", ex.Message);
        Assert.Contains("Hardware items with no templates linked (1)", ex.Message);
        Assert.Contains("MODEL-EMPTY", ex.Message);
        Assert.Contains("Templates that could not be processed (1)", ex.Message);
        Assert.Contains("T-BROKEN", ex.Message);
    }

    [Fact]
    public async Task AssembleAsync_HappyPath_Succeeds()
    {
        var (mfr, desc) = SeedMfrAndDesc();
        var itemA = SeedHardwareItem(mfr, desc, "MODEL-A", "T-A", withWorkingTemplate: true);
        var itemB = SeedHardwareItem(mfr, desc, "MODEL-B", "T-B", withWorkingTemplate: true);

        var request = BuildRequest(new[] { itemA, itemB });

        var result = await BuildService().AssembleAsync(request);

        Assert.Equal(2, result.TemplateSnapshots.Count);
        Assert.True(File.Exists(result.OutputPath));
    }
}
