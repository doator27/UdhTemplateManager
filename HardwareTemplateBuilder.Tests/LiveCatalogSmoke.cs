using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services.Catalogs;
using HardwareTemplateBuilder.Core.Services.Import;
using HardwareTemplateBuilder.Core.Services.TemplateFinder;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace HardwareTemplateBuilder.Tests;

public class LiveCatalogSmoke
{
    private readonly ITestOutputHelper _out;
    public LiveCatalogSmoke(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task Live()
    {
        if (Environment.GetEnvironmentVariable("LIVE_SMOKE") != "1") return;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("live2").Options;
        using var http = TemplateFinderService.CreateHttpClient();
        int mfrId;
        using (var ctx = new AppDbContext(options))
        {
            var m = new HardwareTemplateBuilder.Core.Models.Manufacturer { ManufacturerName = "Detex" };
            ctx.Manufacturers.Add(m);
            ctx.Descriptions.Add(new HardwareTemplateBuilder.Core.Models.Description { DescriptionText = "Exit Devices" });
            ctx.Descriptions.Add(new HardwareTemplateBuilder.Core.Models.Description { DescriptionText = "Closers" });
            ctx.Descriptions.Add(new HardwareTemplateBuilder.Core.Models.Description { DescriptionText = "Alarms" });
            ctx.SaveChanges();
            mfrId = m.Id;
        }
        var svc = new ProductCatalogService(() => new AppDbContext(options), http);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var c1 = svc.Add(mfrId, null, "https://www.detex.com/wp-content/uploads/2022/05/Full-Price-List.pdf", null);
        var c2 = svc.Add(mfrId, null, "https://www.detex.com/wp-content/uploads/2026/07/8-2026-ryobi-price-list.pdf", null);
        foreach (var c in new[] { c1, c2 })
        {
            var n = await svc.IndexAsync(c.Id, null, null, CancellationToken.None);
            _out.WriteLine($"indexed {c.Title}: {n} pages in {sw.Elapsed.TotalSeconds:0.0}s");
        }
        var lookup = new CatalogLookupService(() => new AppDbContext(options));
        foreach (var callout in new[] { "ECL-230X", "ECL-230X W 28", "V40 EI KS", "10-800", "D4551DA-TP-AL", "EAX-500", "V40 x EB x 1R x 99 x 32", "ECL-230D PH", "10/30/40" })
        {
            sw.Restart();
            var r = lookup.Lookup(mfrId, callout);
            _out.WriteLine($"=== {callout} ({sw.ElapsedMilliseconds} ms)\n{r.Summary}");
            foreach (var h in r.Hits.Take(3)) _out.WriteLine($"   - {h.DisplayText} [score {h.Score}]");
        }
        var best = lookup.Lookup(mfrId, "ECL-230X").Hits.FirstOrDefault();
        if (best != null)
        {
            var bytes = await svc.PagePdfAsync(best.CatalogId, best.PageNumber, CancellationToken.None);
            _out.WriteLine($"page pdf: {bytes.Length} bytes");
            var page = new AppDbContext(options).ProductCatalogPages.First(p => p.CatalogId == best.CatalogId && p.PageNumber == best.PageNumber);
            _out.WriteLine("-- page text --\n" + string.Join("\n", page.Text.Split('\n').Take(45)));
        }
    }
}
