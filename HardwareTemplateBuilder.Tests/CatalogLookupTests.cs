using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.Catalogs;
using HardwareTemplateBuilder.Core.Services.Import;
using HardwareTemplateBuilder.Core.Services.TemplateFinder;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for reading product catalog pages and looking callouts up in them.</summary>
public class CatalogLookupTests
{
    private const string CloserPage =
        "LCN  4040XP SERIES\n" +
        "4040XP  Heavy Duty Surface Closer  $412.00\n" +
        "Arms and Options\n" +
        "EDA    Extra Duty Arm               RW/PA   Regular Arm with Parallel Arm Shoe\n" +
        "CUSH   Cush-N-Stop Arm\n" +
        "SCUSH - Spring Cush Arm\n" +
        "Template No.  5-3018  (see page 112)\n";

    [Fact]
    public void Tokens_KeysAndOptionCodes()
    {
        Assert.Equal(new[] { "4040XP" }, CatalogPageAnalyzer.ModelKeys("4040XP EDA RW/PA AL").Take(1));
        Assert.Contains("4040", CatalogPageAnalyzer.ModelKeys("4040XP EDA RW/PA AL"));
        Assert.Equal(new[] { "EDA", "RW/PA" }, CatalogPageAnalyzer.OptionCodes("4040XP EDA RW/PA AL"));
        Assert.Equal(new[] { "QEL" }, CatalogPageAnalyzer.OptionCodes("QEL-9875L-F 06 US26D"));
        Assert.Equal(new[] { "9875LF", "9875L", "9875" }, CatalogPageAnalyzer.ModelKeys("9875L-F 06"));
    }

    [Fact]
    public void ScorePage_PrefersWholeModelAndOptions()
    {
        var options = new[] { "EDA", "RW/PA" };
        int good = CatalogPageAnalyzer.ScorePage(CloserPage, "4040XP", options);
        int other = CatalogPageAnalyzer.ScorePage("1460 Series  Standard Closer\n4040XPT kit", "4040XP", options);
        Assert.True(good > other && other > 0);
        Assert.Equal(0, CatalogPageAnalyzer.ScorePage("1460 Series", "4040XP", options));
    }

    [Fact]
    public void Page_GivesTemplateOptionsAndDescription()
    {
        Assert.Equal(new[] { "5-3018" }, CatalogPageAnalyzer.TemplateNumbers(CloserPage, "4040XP", Array.Empty<string>()));
        Assert.Equal("Extra Duty Arm", CatalogPageAnalyzer.OptionMeaning(CloserPage, "EDA"));
        Assert.Equal("Regular Arm with Parallel Arm Shoe", CatalogPageAnalyzer.OptionMeaning(CloserPage, "RW/PA"));
        Assert.Equal("Spring Cush Arm", CatalogPageAnalyzer.OptionMeaning(CloserPage, "SCUSH"));
        Assert.Null(CatalogPageAnalyzer.OptionMeaning(CloserPage, "HO"));
        Assert.Equal("Heavy Duty Surface Closer", CatalogPageAnalyzer.Description(CloserPage, "4040XP", new[] { "EDA" }));
        Assert.Equal("Alarmed Deadbolt Panic Hardware",
            CatalogPageAnalyzer.Description("ECL-230X SERIES\nAlarmed Deadbolt Panic Hardware\nECL-230X  $1,242", "ECL230X", Array.Empty<string>()));
    }

    [Fact]
    public void TemplateNumbers_FindKnownNumbersOnThePage()
    {
        var page = "SB-5BB1 Hinge\nFor prep see 41-2217 and 613\n";
        var numbers = CatalogPageAnalyzer.TemplateNumbers(page, "5BB1", new[] { "41-2217", "613", "999" });
        Assert.Equal(new[] { "41-2217" }, numbers); // "613" is too short to trust off a template line
    }

    [Fact]
    public void SuggestDescription_PicksTheDeepestFullMatch()
    {
        var descriptions = new List<Description>
        {
            new() { Id = 1, DescriptionText = "Closers" },
            new() { Id = 2, DescriptionText = "Surface Closer", ParentId = 1 },
            new() { Id = 3, DescriptionText = "Concealed Closer", ParentId = 1 },
            new() { Id = 4, DescriptionText = "Hinges" },
        };
        Assert.Equal(2, CatalogPageAnalyzer.SuggestDescriptionId(descriptions, "Heavy Duty Surface Closers"));
        Assert.Equal(1, CatalogPageAnalyzer.SuggestDescriptionId(descriptions, "LCN closer catalog"));
        Assert.Null(CatalogPageAnalyzer.SuggestDescriptionId(descriptions, "Exit devices"));
    }

    [Fact]
    public void CatalogUrlScore_FindsCatalogs()
    {
        Assert.True(TemplateFinderService.CatalogUrlScore("https://x.com/docs/LCN_Price_Book_2026.pdf") > 0);
        Assert.True(TemplateFinderService.CatalogUrlScore("https://x.com/docs/full-catalog.pdf") >
                    TemplateFinderService.CatalogUrlScore("https://x.com/docs/catalog-install-instructions.pdf"));
        Assert.Equal(0, TemplateFinderService.CatalogUrlScore("https://x.com/docs/4040XP_template.pdf"));
        Assert.True(TemplateFinderService.CatalogUrlScore("https://x.com/literature") > 0);
        Assert.Equal("LCN Price Book 2026", ProductCatalogService.TitleFromLink("https://x.com/a/LCN_Price_Book_2026.pdf"));
    }

    [Fact]
    public void Lookup_ReadsTheCatalogAndLinksKnownTemplates()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"HtbCatalogTests_{Guid.NewGuid():N}")
            .Options;
        int mfrId, itemId;
        using (var ctx = new AppDbContext(options))
        {
            var mfr = new Manufacturer { ManufacturerName = "LCN" };
            var closers = new Description { DescriptionText = "Closers" };
            var material = new DoorMaterial { Material = "Both" };
            ctx.AddRange(mfr, closers, material);
            ctx.SaveChanges();
            closers.Children.Add(new Description { DescriptionText = "Surface Closer" });
            var item = new HardwareItem { ManufacturerId = mfr.Id, DescriptionId = closers.Id, ModelNumber = "4040XP RW/PA" };
            var template = new IndividualTemplate
            {
                ManufacturerId = mfr.Id, DescriptionId = closers.Id, DoorMaterialId = material.Id,
                TemplateNumber = "5-3018", PagesToPrint = "1", NumPages = 1,
            };
            ctx.AddRange(item, template);
            ctx.SaveChanges();
            ctx.HardwareItemTemplates.Add(new HardwareItemTemplate { HardwareItemId = item.Id, IndividualTemplateId = template.Id });

            var catalog = new ProductCatalog { ManufacturerId = mfr.Id, Title = "LCN Closers", PageCount = 3, IndexedUtc = DateTime.UtcNow };
            ctx.ProductCatalogs.Add(catalog);
            ctx.SaveChanges();
            void Page(int n, string text) => ctx.ProductCatalogPages.Add(new ProductCatalogPage
            {
                CatalogId = catalog.Id, PageNumber = n, Text = text, SearchKey = ImportText.Key(text),
            });
            Page(1, "Contents\n4040XP ....... 2");
            Page(2, "4040XP  Heavy Duty Surface Closer\nTemplate No. 5-3018");
            Page(3, "Arm options\nEDA  Extra Duty Arm\nRW/PA  Regular Arm w/ PA Shoe");
            ctx.SaveChanges();
            mfrId = mfr.Id;
            itemId = item.Id;
        }

        var service = new CatalogLookupService(() => new AppDbContext(options));
        Assert.True(service.HasCatalogs(mfrId));
        var result = service.Lookup(mfrId, "4040XP EDA RW/PA AL");

        Assert.True(result.Found);
        Assert.Equal(2, result.Hits[0].PageNumber);
        Assert.Equal("Heavy Duty Surface Closer", result.Description);
        var t = Assert.Single(result.Templates);
        Assert.Equal("5-3018", t.Number);
        Assert.NotNull(t.TemplateId);
        Assert.Contains(itemId, t.HardwareItemIds);
        Assert.Contains(result.Options, o => o.Code == "EDA" && o.Meaning == "Extra Duty Arm" && o.PageNumber == 3);
        Assert.Contains(result.Options, o => o.Code == "RW/PA");
        Assert.NotNull(result.SuggestedDescriptionId);
        Assert.Contains("5-3018", result.Summary);
        Assert.False(result.IsClosestMatch);

        // A model the catalog doesn't list falls back to its family, and says so.
        var closest = service.Lookup(mfrId, "4040XPT-EDA");
        Assert.Contains(closest.Options, o => o.Code == "EDA");

        // The import wizard gets the item that uses that template as a suggestion.
        var group = new HardwareImportGroup { Catalog = "4040XP EDA", ManufacturerId = mfrId, Status = ImportMatchStatus.NeedsReview, Reason = "Not found" };
        var knowledge = new HardwareImportKnowledge { Items = { [itemId] = new ImportItemInfo(itemId, mfrId, 1, "4040XP RW/PA") } };
        CatalogImportHints.Apply(new[] { group }, service, knowledge);
        Assert.NotNull(group.CatalogInfo);
        Assert.Equal(itemId, group.HardwareItemId);
        Assert.Contains(group.Suggestions, s => s.HardwareItemId == itemId && s.Score == CatalogImportHints.SuggestionScore);
    }
}
