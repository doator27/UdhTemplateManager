using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.Pdf;
using System.Collections.Generic;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="TemplateSorter"/> and <see cref="WeightTemplateSortStrategy"/>.</summary>
public class TemplateSorterTests
{
    private readonly TemplateSorter _sorter = new(new WeightTemplateSortStrategy());

    // ---------- Helpers ----------

    private static Manufacturer MakeManufacturer(int id, string name) =>
        new Manufacturer { Id = id, ManufacturerName = name };

    /// <summary>
    /// Creates a description with an optional parent to represent a position in the tree.
    /// </summary>
    private static Description MakeDesc(int id, int sortOrder, int? parentId = null) =>
        new Description { Id = id, SortOrder = sortOrder, ParentId = parentId, DescriptionText = $"Desc{id}" };

    private static IndividualTemplate MakeTemplate(
        int id,
        Manufacturer manufacturer,
        int descriptionId,
        string templateNumber = "T") =>
        new IndividualTemplate
        {
            Id             = id,
            ManufacturerId = manufacturer.Id,
            Manufacturer   = manufacturer,
            DescriptionId  = descriptionId,
            TemplateNumber = templateNumber,
            PagesToPrint   = "1"
        };

    // ---------- Within-group ordering ----------

    [Fact]
    public void Sort_SingleGroup_OrdersByDescriptionPath()
    {
        // Descriptions: root A(sort=0), root B(sort=1), child of A: CA(sort=0), CB(sort=1)
        // Paths: CA=[0,0], CB=[0,1], A=[0], B=[1]
        var mfr = MakeManufacturer(1, "Alpha");
        var descA  = MakeDesc(1, sortOrder: 0);
        var descB  = MakeDesc(2, sortOrder: 1);
        var descCA = MakeDesc(3, sortOrder: 0, parentId: 1);  // path [0,0]
        var descCB = MakeDesc(4, sortOrder: 1, parentId: 1);  // path [0,1]

        var allDescs = new Dictionary<int, Description>
        {
            [descA.Id]  = descA,
            [descB.Id]  = descB,
            [descCA.Id] = descCA,
            [descCB.Id] = descCB,
        };

        var templates = new[]
        {
            MakeTemplate(1, mfr, descCB.Id,  "T-CB"),  // path [0,1]
            MakeTemplate(2, mfr, descB.Id,   "T-B"),   // path [1]
            MakeTemplate(3, mfr, descCA.Id,  "T-CA"),  // path [0,0]
            MakeTemplate(4, mfr, descA.Id,   "T-A"),   // path [0]
        };

        var result = _sorter.Sort(templates, allDescs);

        // [0] < [0,0] < [0,1] < [1]
        Assert.Equal(new[] { "T-A", "T-CA", "T-CB", "T-B" },
            result.Select(t => t.TemplateNumber));
    }

    [Fact]
    public void Sort_EqualPath_TiebrokenByTemplateNumberAlpha()
    {
        var mfr  = MakeManufacturer(1, "Alpha");
        var desc = MakeDesc(1, sortOrder: 5);
        var allDescs = new Dictionary<int, Description> { [desc.Id] = desc };

        var templates = new[]
        {
            MakeTemplate(1, mfr, desc.Id, "T-Zebra"),
            MakeTemplate(2, mfr, desc.Id, "T-Acme"),
        };

        var result = _sorter.Sort(templates, allDescs);
        Assert.Equal(new[] { "T-Acme", "T-Zebra" }, result.Select(t => t.TemplateNumber));
    }

    // ---------- Cross-group ordering ----------

    [Fact]
    public void Sort_MultipleGroups_OrdersByGroupMinPath()
    {
        var mfrA = MakeManufacturer(1, "Alpha");  // min path: [1]
        var mfrB = MakeManufacturer(2, "Beta");   // min path: [0]  → should come first

        var descLow  = MakeDesc(1, sortOrder: 0);  // path [0]
        var descHigh = MakeDesc(2, sortOrder: 1);  // path [1]

        var allDescs = new Dictionary<int, Description>
        {
            [descLow.Id]  = descLow,
            [descHigh.Id] = descHigh,
        };

        var templates = new[]
        {
            MakeTemplate(1, mfrA, descHigh.Id, "A1"),
            MakeTemplate(2, mfrB, descLow.Id,  "B1"),
            MakeTemplate(3, mfrB, descHigh.Id, "B2"),
        };

        var result = _sorter.Sort(templates, allDescs);
        Assert.Equal(new[] { "B1", "B2", "A1" }, result.Select(t => t.TemplateNumber));
    }

    [Fact]
    public void Sort_TiedGroupMinKey_TiebrokenByManufacturerNameAlpha()
    {
        var mfrZ = MakeManufacturer(1, "Zebra");
        var mfrA = MakeManufacturer(2, "Acme");

        var desc    = MakeDesc(1, sortOrder: 0);
        var allDescs = new Dictionary<int, Description> { [desc.Id] = desc };

        var templates = new[]
        {
            MakeTemplate(1, mfrZ, desc.Id, "Z1"),
            MakeTemplate(2, mfrA, desc.Id, "A1"),
        };

        var result = _sorter.Sort(templates, allDescs);
        Assert.Equal(new[] { "A1", "Z1" }, result.Select(t => t.TemplateNumber));
    }

    [Fact]
    public void Sort_EmptyInput_ReturnsEmptyList()
    {
        var result = _sorter.Sort(
            Array.Empty<IndividualTemplate>(),
            new Dictionary<int, Description>());
        Assert.Empty(result);
    }
}
