using HardwareTemplateBuilder.Core.Services.TemplateFinder;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for the template finder's link logic (no network).</summary>
public class TemplateFinderTests
{
    [Fact]
    public void PatternLinks_SwapTheNumberInPastFileNames()
    {
        var history = new[]
        {
            ("613", "https://ngp.com/plugins/NGPAdmin/data/products/613_cutsheet.pdf"),
            ("A500", "https://api.abhmfg.com/Storage/ProductDocument/A500%20Template(3).pdf"),
            ("Brochure", "https://example.com/files/catalog.pdf"),
        };
        var links = TemplateFinderService.PatternLinks(history, new[] { "5050C" });

        Assert.Contains("https://ngp.com/plugins/NGPAdmin/data/products/5050C_cutsheet.pdf", links);
        Assert.Contains("https://api.abhmfg.com/Storage/ProductDocument/5050C%20Template(3).pdf", links);
        Assert.Contains("https://api.abhmfg.com/Storage/ProductDocument/5050C%20Template.pdf", links);
        Assert.DoesNotContain(links, l => l.Contains("example.com"));
    }

    [Fact]
    public void Sites_PreferTheManufacturersOwnHosts()
    {
        var sites = TemplateFinderService.Sites("Ives", new[]
        {
            "https://us.allegion.com/a.pdf", "https://us.allegion.com/b.pdf", "https://www.iveshinges.com/c.pdf",
            "https://storage.googleapis.com/x/y",
        });
        Assert.Equal(new[] { "us.allegion.com", "www.iveshinges.com" }, sites);

        Assert.Equal(new[] { "www.trimco.com" }, TemplateFinderService.Sites("Trimco", Array.Empty<string>()));
        Assert.Equal(new[] { "www.accentra.com" }, TemplateFinderService.Sites("Accentra (Yale)", new[] { "https://storage.googleapis.com/a" }));
    }

    [Fact]
    public void UrlScore_RewardsExactNumbersAndTemplates()
    {
        var keys = new[] { "4040XP" };
        int template = TemplateFinderService.UrlScore("https://x.com/LCN_4040XP_Template_123.pdf", keys);
        int plain = TemplateFinderService.UrlScore("https://x.com/LCN_4040XP_spec.pdf", keys);
        int brochure = TemplateFinderService.UrlScore("https://x.com/LCN_4040XP_brochure.pdf", keys);
        Assert.True(template > plain && plain > brochure && brochure > 0);
        Assert.Equal(0, TemplateFinderService.UrlScore("https://x.com/LCN_1460_template.pdf", keys));

        // "613" glued inside a longer number is a weak match.
        Assert.True(TemplateFinderService.UrlScore("https://x.com/613_cutsheet.pdf", new[] { "613" }) >
                    TemplateFinderService.UrlScore("https://x.com/26131_cutsheet.pdf", new[] { "613" }));
    }

    [Fact]
    public void PdfLinks_AreMadeAbsolute()
    {
        var html = "<a href=\"/docs/5BB1-template.pdf\">T</a> <a href='https://cdn.x.com/a/b.PDF?v=2'>B</a> <a href=\"page.html\">P</a>";
        var links = TemplateFinderService.PdfLinks(html, "https://www.iveshinges.com/products/5bb1");
        Assert.Equal(new[] { "https://www.iveshinges.com/docs/5BB1-template.pdf", "https://cdn.x.com/a/b.PDF?v=2" }, links);
    }

    [Fact]
    public void LooksLikePdf_ChecksTheHeader()
    {
        Assert.True(TemplateLinkChecker.LooksLikePdf("%PDF-1.7\n"u8.ToArray()));
        Assert.True(TemplateLinkChecker.LooksLikePdf("\r\n%PDF-1.4"u8.ToArray()));
        Assert.False(TemplateLinkChecker.LooksLikePdf("<!DOCTYPE html><html>"u8.ToArray()));
    }

    [Fact]
    public void Terms_AreDistinctAndSpecificFirst()
    {
        var r = new TemplateSearchRequest { ManufacturerName = "LCN", ModelNumber = "4040XP RWPA", TemplateNumber = "73089" };
        r.Labels.Add("4040XP REGARM");
        r.Labels.Add("4040XP RWPA");
        Assert.Equal(new[] { "4040XP RWPA", "4040XP", "73089", "4040XP REGARM" }, r.Terms());
        Assert.StartsWith("https://www.google.com/search?q=LCN%204040XP%20RWPA%20template%20pdf", r.GoogleSearchUrl());
    }
}

/// <summary>Tests for <see cref="RobotsRules"/>.</summary>
public class RobotsRulesTests
{
    [Fact]
    public void StarGroupRules_AreApplied_LongestMatchWins()
    {
        var r = RobotsRules.Parse("User-agent: Googlebot\nDisallow:\n\nUser-agent: *\nDisallow: /admin\nDisallow: /private/\nAllow: /private/docs\nDisallow: /*.aspx$\n");
        Assert.True(r.IsAllowed("/products/x.pdf"));
        Assert.False(r.IsAllowed("/admin/page"));
        Assert.False(r.IsAllowed("https://site.com/private/a"));
        Assert.True(r.IsAllowed("https://site.com/private/docs/t.pdf"));
        Assert.False(r.IsAllowed("/x/page.aspx"));
        Assert.True(r.IsAllowed("/x/page.aspx?y=1"));
    }

    [Fact]
    public void DisallowAll_BlocksEverything_AndOtherAgentsAreIgnored()
    {
        Assert.False(RobotsRules.Parse("User-agent: *\nDisallow: /").IsAllowed("/anything"));
        Assert.True(RobotsRules.Parse("User-agent: badbot\nDisallow: /").IsAllowed("/anything"));
        Assert.True(RobotsRules.Parse(null).IsAllowed("/anything"));
    }
}

/// <summary>Tests for <see cref="HardwareTemplateBuilder.Core.Services.TemplatePageInfo"/>.</summary>
public class TemplatePageInfoTests
{
    [Fact]
    public void AllPages_AndRangeCounts()
    {
        Assert.Equal("1", HardwareTemplateBuilder.Core.Services.TemplatePageInfo.AllPages(1));
        Assert.Equal("1-6", HardwareTemplateBuilder.Core.Services.TemplatePageInfo.AllPages(6));
        Assert.Equal(4, HardwareTemplateBuilder.Core.Services.TemplatePageInfo.CountInRange("1,3-5"));
        Assert.Null(HardwareTemplateBuilder.Core.Services.TemplatePageInfo.CountInRange("1-"));
        Assert.Null(HardwareTemplateBuilder.Core.Services.TemplatePageInfo.CountInRange(""));
    }

    [Fact]
    public void DefaultMaterial_IsBoth()
    {
        var materials = new[] { new HardwareTemplateBuilder.Core.Models.DoorMaterial { Id = 1, Material = "Metal" }, new HardwareTemplateBuilder.Core.Models.DoorMaterial { Id = 3, Material = "Both" } };
        Assert.Equal(3, HardwareTemplateBuilder.Core.Services.TemplatePageInfo.DefaultMaterial(materials)!.Id);
    }

    [Fact]
    public async Task CountPages_ReadsALocalPdf_AndRejectsOtherFiles()
    {
        var pdf = Path.Combine(Path.GetTempPath(), $"htb_pages_{Guid.NewGuid():N}.pdf");
        var txt = Path.ChangeExtension(pdf, ".txt");
        try
        {
            using (var doc = new PdfSharp.Pdf.PdfDocument()) { doc.AddPage(); doc.AddPage(); doc.AddPage(); doc.Save(pdf); }
            File.WriteAllText(txt, "not a pdf");
            Assert.Equal(3, await HardwareTemplateBuilder.Core.Services.TemplatePageInfo.CountPagesAsync(pdf, null));
            Assert.Null(await HardwareTemplateBuilder.Core.Services.TemplatePageInfo.CountPagesAsync(txt, null));
            Assert.Null(await HardwareTemplateBuilder.Core.Services.TemplatePageInfo.CountPagesAsync(null, "not a link"));
        }
        finally
        {
            File.Delete(pdf);
            File.Delete(txt);
        }
    }
}
