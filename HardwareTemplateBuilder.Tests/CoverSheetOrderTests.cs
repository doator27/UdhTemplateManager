using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.Pdf;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="CoverSheetOrder"/>.</summary>
public class CoverSheetOrderTests
{
    private sealed record Row(string Label, int? DescriptionId, params IndividualTemplate[] Templates);

    // Hinges (sort 0) > Butt hinges (0), Continuous hinges (1); Locks (1); Closers (2)
    private static readonly Dictionary<int, Description> Descriptions = new()
    {
        [1] = new Description { Id = 1, DescriptionText = "Hinges", SortOrder = 0 },
        [2] = new Description { Id = 2, DescriptionText = "Butt", SortOrder = 0, ParentId = 1 },
        [3] = new Description { Id = 3, DescriptionText = "Continuous", SortOrder = 1, ParentId = 1 },
        [4] = new Description { Id = 4, DescriptionText = "Locks", SortOrder = 1 },
        [5] = new Description { Id = 5, DescriptionText = "Closers", SortOrder = 2 },
    };

    private static IndividualTemplate Template(int descriptionId, string number) =>
        new() { DescriptionId = descriptionId, TemplateNumber = number };

    private static List<string> SortLabels(params Row[] rows) =>
        CoverSheetOrder.Sort(rows, r => r.Templates, r => r.DescriptionId, r => r.Label, Descriptions)
            .Select(r => r.Label).ToList();

    [Fact]
    public void OrdersByTemplateDescriptionPath_LikeThePdfBody()
    {
        var labels = SortLabels(
            new Row("closer", 5, Template(5, "C-1")),
            new Row("continuous", 3, Template(3, "H-9")),
            new Row("lock", 4, Template(4, "L-1")),
            new Row("butt", 2, Template(2, "H-1")));

        Assert.Equal(new[] { "butt", "continuous", "lock", "closer" }, labels);
    }

    [Fact]
    public void UsesEarliestTemplate_ThenTemplateNumber_ThenLabel()
    {
        var labels = SortLabels(
            new Row("b-second-template-number", 4, Template(4, "L-2")),
            new Row("multi", 5, Template(5, "C-1"), Template(4, "L-1")),   // its lock template comes first
            new Row("a-same-template", 4, Template(4, "L-2")));

        Assert.Equal(new[] { "multi", "a-same-template", "b-second-template-number" }, labels);
    }

    [Fact]
    public void ItemsWithoutTemplates_UseTheirDescription_AndUnplaceableGoLast()
    {
        var labels = SortLabels(
            new Row("unknown", null),
            new Row("new closer", 5),
            new Row("linked lock", 4, Template(4, "L-1")),
            new Row("new butt", 2));

        Assert.Equal(new[] { "new butt", "linked lock", "new closer", "unknown" }, labels);
    }
}
