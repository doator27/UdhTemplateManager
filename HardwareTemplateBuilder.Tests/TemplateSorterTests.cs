using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services;
using HardwareTemplateBuilder.Core.Services.Pdf;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="TemplateSorter"/> and <see cref="WeightTemplateSortStrategy"/>.</summary>
public class TemplateSorterTests
{
    private readonly TemplateSorter _sorter;

    /// <summary>Initializes the sorter with the default weight-based strategy.</summary>
    public TemplateSorterTests()
    {
        _sorter = new TemplateSorter(new WeightTemplateSortStrategy(new WeightParser()));
    }

    // ---------- Helpers ----------

    private static Manufacturer MakeManufacturer(int id, string name) =>
        new Manufacturer { Id = id, ManufacturerName = name };

    private static Weight MakeWeight(int id, string value) =>
        new Weight { Id = id, WeightValue = value };

    private static IndividualTemplate MakeTemplate(
        int id, Manufacturer manufacturer, Weight weight, string templateNumber = "T") =>
        new IndividualTemplate
        {
            Id = id,
            ManufacturerId = manufacturer.Id,
            Manufacturer = manufacturer,
            WeightId = weight.Id,
            Weight = weight,
            TemplateNumber = templateNumber,
            PagesToPrint = "1"
        };

    // ---------- Tests ----------

    [Fact]
    public void Sort_SingleGroup_OrdersByWeightAscending()
    {
        var mfr = MakeManufacturer(1, "Alpha");
        var templates = new[]
        {
            MakeTemplate(1, mfr, MakeWeight(3, "02.001.001"), "T3"),
            MakeTemplate(2, mfr, MakeWeight(1, "01.001.001"), "T1"),
            MakeTemplate(3, mfr, MakeWeight(2, "01.002.001"), "T2"),
        };

        var result = _sorter.Sort(templates);

        Assert.Equal(new[] { "T1", "T2", "T3" }, result.Select(t => t.TemplateNumber));
    }

    [Fact]
    public void Sort_MultipleGroups_OrdersByGroupMinWeightAscending()
    {
        var mfrA = MakeManufacturer(1, "Alpha");  // min weight 02.001.001
        var mfrB = MakeManufacturer(2, "Beta");   // min weight 01.001.001

        var templates = new[]
        {
            MakeTemplate(1, mfrA, MakeWeight(1, "02.001.001"), "A1"),
            MakeTemplate(2, mfrB, MakeWeight(2, "01.001.001"), "B1"),
            MakeTemplate(3, mfrB, MakeWeight(3, "01.002.001"), "B2"),
        };

        var result = _sorter.Sort(templates);

        // Beta group (lower min weight) should come before Alpha group.
        Assert.Equal(new[] { "B1", "B2", "A1" }, result.Select(t => t.TemplateNumber));
    }

    [Fact]
    public void Sort_TiedGroupMinWeight_TiebrokenByManufacturerNameAlphabetically()
    {
        var mfrZ = MakeManufacturer(1, "Zebra");
        var mfrA = MakeManufacturer(2, "Acme");

        // Both groups have the same minimum weight.
        var sharedWeight = "01.001.001";

        var templates = new[]
        {
            MakeTemplate(1, mfrZ, MakeWeight(1, sharedWeight), "Z1"),
            MakeTemplate(2, mfrA, MakeWeight(2, sharedWeight), "A1"),
        };

        var result = _sorter.Sort(templates);

        // Alphabetical tiebreak: Acme before Zebra.
        Assert.Equal(new[] { "A1", "Z1" }, result.Select(t => t.TemplateNumber));
    }

    [Fact]
    public void Sort_WithinGroupOrderedByWeight_CrossGroupOrderByMinWeight()
    {
        var mfrA = MakeManufacturer(1, "Alpha");
        var mfrB = MakeManufacturer(2, "Beta");

        var templates = new[]
        {
            MakeTemplate(1, mfrA, MakeWeight(1, "01.005.001"), "A-High"),
            MakeTemplate(2, mfrA, MakeWeight(2, "01.001.001"), "A-Low"),
            MakeTemplate(3, mfrB, MakeWeight(3, "02.001.001"), "B1"),
        };

        var result = _sorter.Sort(templates);

        // Alpha group has lower min weight (01.001.001) — comes first, A-Low before A-High.
        Assert.Equal(new[] { "A-Low", "A-High", "B1" }, result.Select(t => t.TemplateNumber));
    }

    [Fact]
    public void Sort_EmptyInput_ReturnsEmptyList()
    {
        var result = _sorter.Sort(Array.Empty<IndividualTemplate>());
        Assert.Empty(result);
    }
}
