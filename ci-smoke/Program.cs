using System.Text.RegularExpressions;
using HardwareTemplateBuilder.Core.Services.TemplateFinder;

using var http = TemplateFinderService.CreateHttpClient();
async Task Probe(string url)
{
    try
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        TemplateFinderService.AddBrowserHeaders(req);
        using var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        var locs = Regex.Matches(body, "<loc>(.*?)</loc>").Select(m => m.Groups[1].Value).ToList();
        Console.WriteLine($"PROBE {(int)resp.StatusCode} {resp.Content.Headers.ContentType?.MediaType} len={body.Length} locs={locs.Count} final={resp.RequestMessage?.RequestUri} <- {url}");
        foreach (var l in locs.Take(8)) Console.WriteLine($"PROBE    loc {l}");
        if (locs.Count == 0) Console.WriteLine("PROBE    body: " + Regex.Replace(body.Length > 600 ? body[..600] : body, @"\s+", " "));
        var pdfs = Regex.Matches(body, @"[^""'\s>]+\.pdf", RegexOptions.IgnoreCase).Select(m => m.Value).Distinct().Take(6).ToList();
        foreach (var p in pdfs) Console.WriteLine($"PROBE    pdf {p}");
    }
    catch (Exception ex) { Console.WriteLine($"PROBE ERR {url}: {ex.Message}"); }
}
foreach (var u in new[]
{
    "https://us.allegion.com/robots.txt", "https://us.allegion.com/sitemap.xml", "https://us.allegion.com/en.sitemap.xml",
    "https://us.allegion.com/en/search.html?q=FS436", "https://us.allegion.com/content/allegion-us/en/search-results.html?searchTerm=FS436",
    "https://www.hagerco.com/robots.txt", "https://www.hagerco.com/sitemap.xml", "https://www.hagerco.com/search?q=780-111",
    "https://www.iveshinges.com/robots.txt", "https://www.lcnclosers.com/robots.txt",
    "https://www.trimcohardware.com/robots.txt", "https://ngp.com/robots.txt",
})
    await Probe(u);
