using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="TemplateFixListService"/>.</summary>
public class TemplateFixListServiceTests
{
    private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"HtbFixListTests_{Guid.NewGuid():N}")
        .Options;

    private AppDbContext CreateContext() => new(_options);

    private sealed record Seed(int BrokenTemplateId, int GoodTemplateId, int ItemA, int ItemB, int ItemC);

    /// <summary>
    /// Items A and B use the broken template; C uses only the good one.
    /// Description tree: Hinges > Butt.
    /// </summary>
    private Seed SeedData()
    {
        using var ctx = CreateContext();
        var mfr = new Manufacturer { ManufacturerName = "Acme" };
        var root = new Description { DescriptionText = "Hinges" };
        ctx.Manufacturers.Add(mfr);
        ctx.Descriptions.Add(root);
        ctx.SaveChanges();
        var leaf = new Description { DescriptionText = "Butt", ParentId = root.Id };
        ctx.Descriptions.Add(leaf);
        ctx.SaveChanges();

        var broken = new IndividualTemplate { ManufacturerId = mfr.Id, DescriptionId = leaf.Id, TemplateNumber = "T-BAD", OnlineLink = "https://example.com/bad.pdf" };
        var good = new IndividualTemplate { ManufacturerId = mfr.Id, DescriptionId = leaf.Id, TemplateNumber = "T-OK", OnlineLink = "https://example.com/ok.pdf" };
        ctx.IndividualTemplates.AddRange(broken, good);

        var a = new HardwareItem { ManufacturerId = mfr.Id, DescriptionId = leaf.Id, ModelNumber = "A-1" };
        var b = new HardwareItem { ManufacturerId = mfr.Id, DescriptionId = leaf.Id, ModelNumber = "B-1" };
        var c = new HardwareItem { ManufacturerId = mfr.Id, DescriptionId = leaf.Id, ModelNumber = "C-1" };
        ctx.HardwareItems.AddRange(a, b, c);
        ctx.SaveChanges();

        ctx.HardwareItemTemplates.AddRange(
            new HardwareItemTemplate { HardwareItemId = a.Id, IndividualTemplateId = broken.Id },
            new HardwareItemTemplate { HardwareItemId = b.Id, IndividualTemplateId = broken.Id },
            new HardwareItemTemplate { HardwareItemId = c.Id, IndividualTemplateId = good.Id });
        ctx.SaveChanges();
        return new Seed(broken.Id, good.Id, a.Id, b.Id, c.Id);
    }

    private void RecordFailure(int templateId)
    {
        using var ctx = CreateContext();
        TemplateFixListService.Record(ctx, new[] { new RefreshFailure { TemplateId = templateId, ErrorMessage = "Not a valid PDF" } });
    }

    private List<TemplateFixItem> Outstanding()
    {
        using var ctx = CreateContext();
        return TemplateFixListService.GetOutstanding(ctx);
    }

    [Fact]
    public void Record_ListsEveryItemUsingAFailedTemplate()
    {
        var seed = SeedData();
        RecordFailure(seed.BrokenTemplateId);

        var items = Outstanding();

        Assert.Equal(new[] { seed.ItemA, seed.ItemB }, items.Select(i => i.HardwareItemId).ToArray());
        var first = items[0];
        Assert.Equal("Acme", first.ManufacturerName);
        Assert.Equal("Hinges > Butt", first.Description);
        Assert.Equal("A-1", first.ModelNumber);
        var failed = Assert.Single(first.FailedTemplates);
        Assert.Equal("T-BAD", failed.TemplateNumber);
        Assert.Equal("Not a valid PDF", failed.Error);
    }

    [Fact]
    public void ItemDropsOff_WhenItGetsANewTemplate()
    {
        var seed = SeedData();
        RecordFailure(seed.BrokenTemplateId);

        using (var ctx = CreateContext())
        {
            ctx.HardwareItemTemplates.Add(new HardwareItemTemplate { HardwareItemId = seed.ItemA, IndividualTemplateId = seed.GoodTemplateId });
            ctx.SaveChanges();
        }

        Assert.Equal(new[] { seed.ItemB }, Outstanding().Select(i => i.HardwareItemId).ToArray());
    }

    [Fact]
    public void ItemDropsOff_WhenTheBrokenTemplateIsUnlinked()
    {
        var seed = SeedData();
        RecordFailure(seed.BrokenTemplateId);

        using (var ctx = CreateContext())
        {
            ctx.HardwareItemTemplates.RemoveRange(ctx.HardwareItemTemplates.Where(l => l.HardwareItemId == seed.ItemB));
            ctx.SaveChanges();
        }

        Assert.Equal(new[] { seed.ItemA }, Outstanding().Select(i => i.HardwareItemId).ToArray());
    }

    [Fact]
    public void AllItemsDropOff_WhenTheBrokenTemplateItselfIsFixed()
    {
        var seed = SeedData();
        RecordFailure(seed.BrokenTemplateId);

        using (var ctx = CreateContext())
        {
            ctx.IndividualTemplates.Find(seed.BrokenTemplateId)!.OnlineLink = "https://example.com/fixed.pdf";
            ctx.SaveChanges();
        }

        Assert.Empty(Outstanding());
    }

    [Fact]
    public void Dismiss_RemovesOnlyThatItem()
    {
        var seed = SeedData();
        RecordFailure(seed.BrokenTemplateId);

        using (var ctx = CreateContext())
            TemplateFixListService.Dismiss(ctx, seed.ItemA);

        Assert.Equal(new[] { seed.ItemB }, Outstanding().Select(i => i.HardwareItemId).ToArray());
    }

    [Fact]
    public void Record_WithNoFailures_ClearsTheList()
    {
        var seed = SeedData();
        RecordFailure(seed.BrokenTemplateId);
        using (var ctx = CreateContext())
            TemplateFixListService.Record(ctx, Array.Empty<RefreshFailure>());

        Assert.Empty(Outstanding());
    }
}
