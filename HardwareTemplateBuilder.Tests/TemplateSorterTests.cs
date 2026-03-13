using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.Pdf;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="TemplateSorter"/> and <see cref="WeightTemplateSortStrategy"/>.</summary>
public class TemplateSorterTests
{
    private readonly TemplateSorter _sorter = new(new WeightTemplateSortStrategy());

    // ---------- Helpers ----------

    private static Manufacturer MakeManufacturer(int id, string name) =>
        new Manufacturer { Id = id, ManufacturerName = name };

    private static IndividualTemplate MakeTemplate(
        int id, Manufacturer manufacturer, int sortOrder, string templateNumber = "T") =>
        new IndividualTemplate
        {
            Id = id,
            ManufacturerId = manufacturer.Id,
            Manufacturer = manufacturer,
            Description = new Description { SortOrder = sortOrder },
            TemplateNumber = templateNumber,
            PagesToPrint = "1"
        };

    // ---------- Tests ----------

    [Fact]
    public void Sort_SingleGroup_OrdersBySortOrderAscending()
    {
        var mfr = MakeManufacturer(1, "Alpha");
        var templates = new[]
        {
            MakeTemplate(1, mfr, 20, "T3"),
            MakeTemplate(2, mfr, 10, "T1"),
            MakeTemplate(3, mfr, 15, "T2"),
        };

        var result = _sorter.Sort(templates);

        Assert.Equal(new[] { "T1", "T2", "T3" }, result.Select(t => t.TemplateNumber));
    }

    [Fact]
    public void Sort_MultipleGroups_OrdersByGroupMinSortOrderAscending()
    {
        var mfrA = MakeManufacturer(1, "Alpha");  // min sort order 20
        var mfrB = MakeManufacturer(2, "Beta");   // min sort order 10

        var templates = new[]
        {
            MakeTemplate(1, mfrA, 20, "A1"),
            MakeTemplate(2, mfrB, 10, "B1"),
            MakeTemplate(3, mfrB, 15, "B2"),
        };

        var result = _sorter.Sort(templates);

        // Beta group (lower min sort order) should come before Alpha group.
        Assert.Equal(new[] { "B1", "B2", "A1" }, result.Select(t => t.TemplateNumber));
    }

    [Fact]
    public void Sort_TiedGroupMinSortOrder_TiebrokenByManufacturerNameAlphabetically()
    {
        var mfrZ = MakeManufacturer(1, "Zebra");
        var mfrA = MakeManufacturer(2, "Acme");

        // Both groups have the same minimum sort order.
        var templates = new[]
        {
            MakeTemplate(1, mfrZ, 5, "Z1"),
            MakeTemplate(2, mfrA, 5, "A1"),
        };

        var result = _sorter.Sort(templates);

        // Alphabetical tiebreak: Acme before Zebra.
        Assert.Equal(new[] { "A1", "Z1" }, result.Select(t => t.TemplateNumber));
    }

    [Fact]
    public void Sort_WithinGroupOrderedBySortOrder_CrossGroupOrderByMinSortOrder()
    {
        var mfrA = MakeManufacturer(1, "Alpha");
        var mfrB = MakeManufacturer(2, "Beta");

        var templates = new[]
        {
            MakeTemplate(1, mfrA, 50, "A-High"),
            MakeTemplate(2, mfrA, 10, "A-Low"),
            MakeTemplate(3, mfrB, 20, "B1"),
        };

        var result = _sorter.Sort(templates);

        // Alpha group has lower min sort order (10) — comes first, A-Low before A-High.
        Assert.Equal(new[] { "A-Low", "A-High", "B1" }, result.Select(t => t.TemplateNumber));
    }

    [Fact]
    public void Sort_EqualSortOrder_TiebrokenByTemplateNumberAlphabetically()
    {
        var mfr = MakeManufacturer(1, "Alpha");
        var templates = new[]
        {
            MakeTemplate(1, mfr, 5, "T-Zebra"),
            MakeTemplate(2, mfr, 5, "T-Acme"),
        };

        var result = _sorter.Sort(templates);

        Assert.Equal(new[] { "T-Acme", "T-Zebra" }, result.Select(t => t.TemplateNumber));
    }

    [Fact]
    public void Sort_EmptyInput_ReturnsEmptyList()
    {
        var result = _sorter.Sort(Array.Empty<IndividualTemplate>());
        Assert.Empty(result);
    }
}
