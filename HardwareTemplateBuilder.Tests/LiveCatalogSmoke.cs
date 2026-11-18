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
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("live").Options;
        using var http = TemplateFinderService.CreateHttpClient();
        var finder = new TemplateFinderService(http, () => new AppDbContext(options));
        var all = new List<string>();
        foreach (var (name, mfrId) in new[] { ("Trimco", 1), ("NGP", 2), ("Rockwood", 3), ("Pemko", 4), ("Hager", 5), ("LCN", 6), ("Detex", 7), ("Norton", 8) })
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var links = await finder.FindCatalogLinksAsync(mfrId, name, null, cts.Token);
                _out.WriteLine($"== {name}: {links.Count} links; blocked: {string.Join(",", finder.BlockedSites)}");
                foreach (var l in links.Take(8)) _out.WriteLine($"   {l.Score} {l.Url}");
                all.AddRange(links.Take(2).Select(l => l.Url));
            }
            catch (Exception ex) { _out.WriteLine($"== {name}: {ex.GetType().Name} {ex.Message}"); }
        }

        foreach (var url in all.Take(4))
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(url);
                var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
                File.WriteAllBytes(path, bytes);
                var text = await HardwarePdfTextReader.ReadAsync(path, null, null, CancellationToken.None);
                _out.WriteLine($"## {url}: {bytes.Length / 1024} KB, {text.Pages.Count} pages");
                var pages = text.Pages.Select(ProductCatalogService.PageText).ToList();
                // pick model-like tokens from a middle page
                var mid = pages.Skip(pages.Count / 3).FirstOrDefault(p => p.Length > 500) ?? pages[0];
                _out.WriteLine("-- sample page --\n" + string.Join("\n", mid.Split('\n').Take(25)));
                var models = System.Text.RegularExpressions.Regex.Matches(mid, @"\b[A-Z]{0,3}\d{2,5}[A-Z]{0,4}\b").Select(m => m.Value).Distinct().Take(4).ToList();
                foreach (var model in models)
                {
                    var key = ImportText.Key(model);
                    var best = pages.Select((p, i) => (i, s: CatalogPageAnalyzer.ScorePage(p, key, Array.Empty<string>()))).Where(x => x.s > 0).OrderByDescending(x => x.s).Take(2).ToList();
                    foreach (var (i, s) in best)
                        _out.WriteLine($"   {model}: page {i + 1} score {s} desc={CatalogPageAnalyzer.Description(pages[i], key, Array.Empty<string>())} templates={string.Join(",", CatalogPageAnalyzer.TemplateNumbers(pages[i], key, Array.Empty<string>()))}");
                }
            }
            catch (Exception ex) { _out.WriteLine($"## {url}: {ex.Message}"); }
        }
    }
}
