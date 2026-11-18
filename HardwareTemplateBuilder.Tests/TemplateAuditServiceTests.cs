using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.TemplateAudit;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for the template description audit (<see cref="TemplateAuditService"/>).</summary>
public class TemplateAuditServiceTests
{
    private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"HtbTemplateAudit_{Guid.NewGuid():N}").Options;

    private (int Exit, int Rim, int Mortise, int Closers, int Von, int Lcn, int T1, int T2, int T3, int Item) Seed()
    {
        using var ctx = new AppDbContext(_options);
        var exit = new Description { DescriptionText = "Exit Devices", SortOrder = 1 };
        var closers = new Description { DescriptionText = "Closers", SortOrder = 2 };
        ctx.Descriptions.AddRange(exit, closers);
        ctx.SaveChanges();
        var rim = new Description { DescriptionText = "Rim", ParentId = exit.Id, SortOrder = 1 };
        var mortise = new Description { DescriptionText = "Mortise", ParentId = exit.Id, SortOrder = 2 };
        ctx.Descriptions.AddRange(rim, mortise);
        var von = new Manufacturer { ManufacturerName = "Von Duprin" };
        var lcn = new Manufacturer { ManufacturerName = "LCN" };
        ctx.Manufacturers.AddRange(von, lcn);
        var mat = new DoorMaterial { Material = "Both" };
        ctx.DoorMaterials.Add(mat);
        ctx.SaveChanges();

        var t1 = new IndividualTemplate { ManufacturerId = von.Id, DescriptionId = rim.Id, TemplateNumber = "99-RIM", DoorMaterialId = mat.Id, PagesToPrint = "1" };
        var t2 = new IndividualTemplate { ManufacturerId = von.Id, DescriptionId = exit.Id, TemplateNumber = "98-MORT", DoorMaterialId = mat.Id, PagesToPrint = "1-2" };
        var t3 = new IndividualTemplate { ManufacturerId = lcn.Id, DescriptionId = closers.Id, TemplateNumber = "4040XP", DoorMaterialId = mat.Id, PagesToPrint = "1" };
        ctx.IndividualTemplates.AddRange(t1, t2, t3);
        var item = new HardwareItem { ManufacturerId = von.Id, DescriptionId = exit.Id, ModelNumber = "9875" };
        ctx.HardwareItems.Add(item);
        ctx.SaveChanges();
        ctx.HardwareItemTemplates.Add(new HardwareItemTemplate { HardwareItemId = item.Id, IndividualTemplateId = t2.Id });
        ctx.HardwareItemTemplates.Add(new HardwareItemTemplate { HardwareItemId = item.Id, IndividualTemplateId = t1.Id });
        ctx.SaveChanges();
        return (exit.Id, rim.Id, mortise.Id, closers.Id, von.Id, lcn.Id, t1.Id, t2.Id, t3.Id, item.Id);
    }

    [Fact]
    public void LoadsTreeFiltersAndFlagsItemsThatDiffer()
    {
        var s = Seed();
        using var ctx = new AppDbContext(_options);
        var rows = TemplateAuditService.Load(ctx);
        Assert.Equal(3, rows.Count);
        var rim = rows.Single(r => r.TemplateId == s.T1);
        Assert.Equal("Exit Devices > Rim", rim.DescriptionPath);
        Assert.True(rim.ItemDescriptionsDiffer);
        Assert.False(rows.Single(r => r.TemplateId == s.T2).ItemDescriptionsDiffer);

        var von = TemplateAuditService.Filter(rows, s.Von, null, null, null, false);
        var tree = TemplateAuditService.DescriptionTree(von, ctx.Descriptions.ToList());
        Assert.Equal(new int?[] { null, s.Exit, s.Rim }, tree.Select(d => d.DescriptionId));
        Assert.Equal(2, tree[1].Count);
        Assert.Equal(1, tree[1].DirectCount);

        var exitAll = TemplateAuditService.Subtree(s.Exit, ctx.Descriptions.ToList());
        Assert.Equal(2, TemplateAuditService.Filter(rows, null, exitAll, null, null, false).Count);
        Assert.Single(TemplateAuditService.Filter(rows, null, null, null, "9875", true));
    }

    [Fact]
    public void MarksAndChangesDescriptions()
    {
        var s = Seed();
        using (var ctx = new AppDbContext(_options))
        {
            TemplateAuditService.Mark(ctx, new[] { s.T2 }, TemplateAuditStatus.NeedsChange, "Move to Mortise", "pat");
            TemplateAuditService.Mark(ctx, new[] { s.T3 }, TemplateAuditStatus.Ok, null, "pat");
        }
        using (var ctx = new AppDbContext(_options))
        {
            var rows = TemplateAuditService.Load(ctx);
            Assert.Equal("Move to Mortise", rows.Single(r => r.TemplateId == s.T2).Mark!.Note);
            Assert.Single(TemplateAuditService.Filter(rows, null, null, TemplateAuditStatus.NotReviewed, null, false));
            Assert.Single(TemplateAuditService.Filter(rows, null, null, null, "mortise", false).Where(r => r.TemplateId == s.T2));

            var (t, i) = TemplateAuditService.SetDescription(ctx, new[] { s.T2 }, s.Mortise, alsoItems: true);
            Assert.Equal((1, 1), (t, i));
            // Back to Not reviewed with no note removes the mark.
            TemplateAuditService.Mark(ctx, new[] { s.T3 }, TemplateAuditStatus.NotReviewed, "", "pat");
        }
        using (var ctx = new AppDbContext(_options))
        {
            Assert.Equal(s.Mortise, ctx.IndividualTemplates.Single(x => x.Id == s.T2).DescriptionId);
            Assert.Equal(s.Mortise, ctx.HardwareItems.Single(x => x.Id == s.Item).DescriptionId);
            Assert.False(TemplateAuditService.LoadMarks(ctx).ContainsKey(s.T3));
            var csv = TemplateAuditService.ToCsv(TemplateAuditService.Load(ctx));
            Assert.Contains("Von Duprin,98-MORT,Exit Devices > Mortise,Needs change,Move to Mortise", csv);
        }
    }
}
