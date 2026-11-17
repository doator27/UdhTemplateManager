using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.Import;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="HardwareImportMatcher"/> and <see cref="HardwareImportLearningService"/>.</summary>
public class PdfImportMatcherTests
{
    private const int Ives = 1, Lcn = 2, Gri = 3, Marks = 4;
    private const int Hinge = 10, Closer = 11, Contact = 12, Mortise = 13, Stop = 14;

    private static HardwareImportKnowledge Knowledge(params PdfImportItemMapping[] mappings)
    {
        var items = new[]
        {
            new ImportItemInfo(Hinge, Ives, 100, "5BB1 4.5"),
            new ImportItemInfo(Closer, Lcn, 101, "4040XP RWPA"),
            new ImportItemInfo(Contact, Gri, 102, "8080T"),
            new ImportItemInfo(Mortise, Marks, 103, "5RW Series"),
            new ImportItemInfo(Stop, Ives, 104, "FS410"),
        }.ToDictionary(i => i.Id);
        var labels = new[]
        {
            new ImportLabelUse(Hinge, "5BB1 4-1/2 x 4-1/2", ImportText.Key("5BB1 4-1/2 x 4-1/2"), 7),
            new ImportLabelUse(Closer, "4040XP REGARM", ImportText.Key("4040XP REGARM"), 3),
            new ImportLabelUse(Mortise, "5RS-853-N \"Robodue\"", ImportText.Key("5RS-853-N \"Robodue\""), 2),
        }.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.ToList());
        return new HardwareImportKnowledge
        {
            Manufacturers = new() { [Ives] = "Ives", [Lcn] = "LCN", [Gri] = "GRI", [Marks] = "Marks" },
            Items = items,
            LabelsByKey = labels,
            Mappings = mappings.ToDictionary(m => (m.ManufacturerCode, m.SourceKey)),
            Threshold = 5,
        };
    }

    private static ParsedHardwareLine Line(string code, string catalog, string? handing = null, string? mfrName = null) =>
        new() { ManufacturerCode = code, Catalog = catalog, Handing = handing, ManufacturerName = mfrName, GenericName = "Item", Quantity = 1 };

    [Fact]
    public void ExactLabelOrModel_IsMatchedAutomatically_WithTheRightLabel()
    {
        var groups = HardwareImportMatcher.Match(new[]
        {
            Line("IV", "5BB1 4 1/2 x 4 1/2"),
            Line("LC", "4040XP REGARM"),
            Line("IV", "FS410"),
        }, Knowledge());

        Assert.All(groups, g => Assert.Equal(ImportMatchStatus.Matched, g.Status));
        Assert.Equal((Hinge, "5BB1 4-1/2 x 4-1/2"), (groups[0].HardwareItemId!.Value, groups[0].Label));
        Assert.Equal((Closer, "4040XP REGARM"), (groups[1].HardwareItemId!.Value, groups[1].Label));
        Assert.Equal((Stop, ""), (groups[2].HardwareItemId!.Value, groups[2].Label));
    }

    [Fact]
    public void HandingVariants_AreOneCallout()
    {
        var groups = HardwareImportMatcher.Match(new[]
        {
            Line("MK01", "5RS-853-N \"ROBODUE\" 2 3/4\"BS", "LH"),
            Line("MK01", "5RS-853-N \"ROBODUE\" 2 3/4\"BS", "RHR"),
        }, Knowledge());

        var g = Assert.Single(groups);
        Assert.Equal(2, g.Lines.Count);
        Assert.Equal(Marks, g.ManufacturerId);
        Assert.Equal(ImportMatchStatus.NeedsReview, g.Status);
        Assert.Equal(Mortise, g.HardwareItemId);                  // best suggestion pre-selected
        Assert.Equal("5RS-853-N \"Robodue\"", g.Label);           // past label it starts with
    }

    [Fact]
    public void ManufacturerNamedInTheCallout_IsUsed()
    {
        var g = Assert.Single(HardwareImportMatcher.Match(new[] { Line("VA01", "8080-T-W 3/4\" GRI", mfrName: "Various - 'Pick a Supplier'") }, Knowledge()));
        Assert.Equal(Gri, g.ManufacturerId);
        Assert.Equal(Contact, g.HardwareItemId);
    }

    [Fact]
    public void PdfCodeListName_ResolvesManufacturer()
    {
        var k = Knowledge();
        Assert.Equal(Lcn, HardwareImportMatcher.ResolveManufacturer(new HardwareImportGroup { ManufacturerCode = "XX", PdfManufacturerName = "LCN Closers", Catalog = "" }, k));
        Assert.Equal(Ives, HardwareImportMatcher.ResolveManufacturer(new HardwareImportGroup { ManufacturerCode = "IV", Catalog = "" }, k));
        Assert.Null(HardwareImportMatcher.ResolveManufacturer(new HardwareImportGroup { ManufacturerCode = "QQ", Catalog = "123" }, k));
    }

    [Fact]
    public void LearnedMapping_IsAutomaticOnlyAtTheThreshold()
    {
        var key = ImportText.Key("4040XP REGARM ST-");
        PdfImportItemMapping Mapping(int count) => new()
        {
            ManufacturerCode = "LC", SourceKey = key, HardwareItemId = Closer, Label = "4040XP REGARM", ConfirmCount = count,
        };

        var below = Assert.Single(HardwareImportMatcher.Match(new[] { Line("LC", "4040XP REGARM ST-") }, Knowledge(Mapping(4))));
        Assert.Equal(ImportMatchStatus.NeedsReview, below.Status);
        Assert.Equal(Closer, below.HardwareItemId);

        var at = Assert.Single(HardwareImportMatcher.Match(new[] { Line("LC", "4040XP REGARM ST-") }, Knowledge(Mapping(5))));
        Assert.Equal(ImportMatchStatus.Matched, at.Status);
        Assert.Equal("4040XP REGARM", at.Label);
    }

    [Fact]
    public void LearnedSkip_IsSkippedAtTheThreshold()
    {
        var skip = new PdfImportItemMapping { ManufacturerCode = "MK01", SourceKey = ImportText.Key("RC1182"), IsSkip = true, ConfirmCount = 5 };
        var g = Assert.Single(HardwareImportMatcher.Match(new[] { Line("MK01", "RC1182") }, Knowledge(skip)));
        Assert.Equal(ImportMatchStatus.Skipped, g.Status);
    }

    [Fact]
    public void Learning_CountsRepeatsAndRestartsOnAChange()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"HtbImportLearn_{Guid.NewGuid():N}").Options;
        HardwareImportGroup Group(int item, string label) => new()
        {
            ManufacturerCode = "LC", Catalog = "4040XP REGARM ST-", SourceKey = ImportText.Key("4040XP REGARM ST-"), HardwareItemId = item, Label = label,
        };
        (int, string)? Info(int id) => (Lcn, id == Closer ? "4040XP RWPA" : "Other");

        for (int i = 0; i < 3; i++)
        {
            using var ctx = new AppDbContext(options);
            HardwareImportLearningService.Record(ctx, new[] { Group(Closer, "4040XP REGARM") }, Info);
        }
        using (var ctx = new AppDbContext(options))
        {
            var m = ctx.PdfImportItemMappings.Single();
            Assert.Equal(3, m.ConfirmCount);
            Assert.Equal(3, ctx.PdfImportManufacturerAliases.Single(a => a.Code == "LC").ConfirmCount);
            HardwareImportLearningService.Record(ctx, new[] { Group(99, "4040XP REGARM") }, Info);
        }
        using (var ctx = new AppDbContext(options))
        {
            var m = ctx.PdfImportItemMappings.Single();
            Assert.Equal(1, m.ConfirmCount);
            Assert.Equal(99, m.HardwareItemId);
        }
    }

    [Fact]
    public void DefaultLabel_IsBlankForThePlainModelNumber()
    {
        Assert.Equal("", HardwareImportMatcher.DefaultLabel("188S-BK 18'", "188S-BK"));
        Assert.Equal("4040XP REGARM", HardwareImportMatcher.DefaultLabel("4040XP REGARM", "4040XP RWPA"));
        Assert.Equal("539AA", HardwareImportMatcher.DefaultLabel("539AA 36\"", "539BK"));
    }

    [Fact]
    public void ImportText_KeysIgnoreSpacingAndOcrConfusions()
    {
        Assert.Equal(ImportText.Key("5BB1 4-1/2 x 4-1/2"), ImportText.Key("5BB1 4 1/2 x 4 1/2"));
        Assert.Equal(ImportText.Key("AEB0IPBA4A8"), ImportText.Key("AEBOIPBA4A8"));
        Assert.Equal("S501", ImportText.CleanManufacturerCode("$501"));
        Assert.Equal("MK01", ImportText.CleanManufacturerCode("MKO1"));
        Assert.Equal("LC", ImportText.CleanManufacturerCode("Lc"));
        Assert.Equal("4040XP CUSH", ImportText.StripSize("4040XP CUSH"));
        Assert.Equal("320N", ImportText.StripSize("320N X 36\""));
    }
}
