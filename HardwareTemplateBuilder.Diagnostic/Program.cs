// Diagnostic: probe live Frascio sitemap and alternative URLs for Rock York / Omnia.
using System.Net.Http;
using System.Text.RegularExpressions;

var handler = new System.Net.Http.SocketsHttpHandler();
handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

static void H(HttpRequestMessage req, string? referer = null)
{
    req.Headers.TryAddWithoutValidation("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*;q=0.8");
    req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.5");
    if (referer != null) req.Headers.TryAddWithoutValidation("Referer", referer);
}

async Task<(bool isPdf, int code, string ct, string preview, byte[] bytes)> FetchAsync(string url, string? referer = null)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    H(req, referer);
    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    var bytes = await resp.Content.ReadAsByteArrayAsync();
    var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
    bool isPdf = bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46;
    var preview = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 800))
                    .Replace("\r", "").Replace("\n", " ");
    return (isPdf, (int)resp.StatusCode, ct, preview, bytes);
}

// -- 1. Frascio document-sitemap.xml -----------------------------------------------
Console.WriteLine("=== 1. Frascio document-sitemap.xml ===");
try
{
    using var req = new HttpRequestMessage(HttpMethod.Get, "https://frinternational.com/document-sitemap.xml");
    H(req, "https://frinternational.com/");
    using var resp = await http.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();
    Console.WriteLine($"Status: {(int)resp.StatusCode}  Length: {body.Length}");

    var locs = Regex.Matches(body, @"<loc>(.*?)</loc>", RegexOptions.Singleline);
    Console.WriteLine($"Total <loc> entries: {locs.Count}");
    Console.WriteLine();

    // All entries — scan for anything door-prep / dp- related
    var targets = new[] { "dp-", "door-prep", "door-stop", "template", "tstk", "salto", "data-sheet" };
    Console.WriteLine("Matching entries:");
    foreach (Match m in locs)
    {
        var url = m.Groups[1].Value.Trim();
        if (targets.Any(t => url.Contains(t, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"  {url}");
    }
    Console.WriteLine();
    Console.WriteLine("ALL entries:");
    foreach (Match m in locs)
        Console.WriteLine($"  {m.Groups[1].Value.Trim()}");
}
catch (Exception ex) { Console.WriteLine($"ERROR: {ex.Message}"); }

// -- 2. Frascio: also try http variant ---------------------------------------------
Console.WriteLine();
Console.WriteLine("=== 2. Frascio document-sitemap.xml (http) ===");
try
{
    using var req = new HttpRequestMessage(HttpMethod.Get, "http://frinternational.com/document-sitemap.xml");
    H(req, "https://frinternational.com/");
    using var resp = await http.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();
    Console.WriteLine($"Status: {(int)resp.StatusCode}  Length: {body.Length}");
    var locs = Regex.Matches(body, @"<loc>(.*?)</loc>", RegexOptions.Singleline);
    Console.WriteLine($"Total <loc> entries: {locs.Count}");
    foreach (Match m in locs)
        Console.WriteLine($"  {m.Groups[1].Value.Trim()}");
}
catch (Exception ex) { Console.WriteLine($"ERROR: {ex.Message}"); }

// -- 3. Frascio: probe known page slugs for embedded PDF links ---------------------
Console.WriteLine();
Console.WriteLine("=== 3. Frascio page probes (look for PDF links) ===");
var frascioPages = new[]
{
    "https://frinternational.com/dp-202/",
    "https://frinternational.com/dp-156/",
    "https://frinternational.com/dp-185/",
    "https://frinternational.com/dp-216/",
    "https://frinternational.com/dp-328/",
    "https://frinternational.com/dp-394/",
    "https://frinternational.com/dp-360/",
    "https://frinternational.com/door-stop/",
    "https://frinternational.com/tstk/",
    "https://frinternational.com/salto/",
    "https://frinternational.com/downloads/",
    "https://frinternational.com/installation-templates/",
    "https://frinternational.com/resources/",
};
foreach (var url in frascioPages)
{
    try
    {
        var (isPdf, code, ct, preview, _) = await FetchAsync(url, "https://frinternational.com/");
        // Extract PDF hrefs from HTML preview
        var links = Regex.Matches(preview, @"href=[""']([^""']+\.pdf[^""']*)["" ']", RegexOptions.IgnoreCase);
        Console.WriteLine($"  {code} {url}  pdfLinks={links.Count}");
        foreach (Match m in links) Console.WriteLine($"    -> {m.Groups[1].Value}");
    }
    catch (Exception ex) { Console.WriteLine($"  ERR {url}: {ex.Message}"); }
}

// -- 4. Frascio: direct wp-content upload path guesses ----------------------------
Console.WriteLine();
Console.WriteLine("=== 4. Frascio: direct upload path guesses ===");
var frascioGuesses = new[]
{
    "https://frinternational.com/wp-content/uploads/DP-202.pdf",
    "https://frinternational.com/wp-content/uploads/dp-202.pdf",
    "https://frinternational.com/wp-content/uploads/DP202.pdf",
    "https://frinternational.com/wp-content/uploads/DP-156.pdf",
    "https://frinternational.com/wp-content/uploads/dp-156.pdf",
    "https://frinternational.com/wp-content/uploads/DP-185.pdf",
    "https://frinternational.com/wp-content/uploads/DP-216.pdf",
    "https://frinternational.com/wp-content/uploads/DP-328.pdf",
    "https://frinternational.com/wp-content/uploads/DP-394.pdf",
    "https://frinternational.com/wp-content/uploads/DP-360.pdf",
    "https://frinternational.com/wp-content/uploads/door-stop.pdf",
    "https://frinternational.com/wp-content/uploads/TSTK.pdf",
    "https://frinternational.com/wp-content/uploads/tstk.pdf",
    "https://frinternational.com/wp-content/uploads/salto.pdf",
    // Year-prefixed variants
    "https://frinternational.com/wp-content/uploads/2024/DP-202.pdf",
    "https://frinternational.com/wp-content/uploads/2025/DP-202.pdf",
    "https://frinternational.com/wp-content/uploads/2024/DP-185.pdf",
    "https://frinternational.com/wp-content/uploads/2025/DP-185.pdf",
};
foreach (var url in frascioGuesses)
{
    try
    {
        var (isPdf, code, ct, _, _) = await FetchAsync(url);
        if (code != 404) Console.WriteLine($"  {code} isPdf={isPdf} {url}");
    }
    catch (Exception ex) { Console.WriteLine($"  ERR {url}: {ex.Message}"); }
}
Console.WriteLine("  (only non-404 results shown above)");

// -- 5. Rock York: try manufacturer + ezconcept alternatives ----------------------
Console.WriteLine();
Console.WriteLine("=== 5. Rock York alternative URLs ===");
var ryAlts = new[]
{
    ("RYST80F/120F", "https://ezconcept.com/wp-content/uploads/sites/5/2024/01/RocYork-RYST80F-RYST120F-Installation-Guide-EZ_US_0226.pdf"),
    ("RYST60/60F",   "https://ezconcept.com/wp-content/uploads/sites/5/2024/01/RocYork-RYST60-RYST60F-Installation-Guide-EZ_US_0226.pdf"),
    ("RY80",         "https://ezconcept.com/wp-content/uploads/sites/5/2025/04/RocYork-RY80-Installation-Instructions-EZ_US_II_WB_JAN18.pdf"),
    ("RYST80F no-s", "https://ezconcept.com/wp-content/uploads/RocYork-RYST80F-RYST120F-Installation-Guide-EZ_US_0226.pdf"),
    ("RY80 no-s",    "https://ezconcept.com/wp-content/uploads/RocYork-RY80-Installation-Instructions-EZ_US_II_WB_JAN18.pdf"),
};
foreach (var (label, url) in ryAlts)
{
    try
    {
        var (isPdf, code, ct, _, _) = await FetchAsync(url, "https://ezconcept.com/");
        Console.WriteLine($"  {code} isPdf={isPdf} [{label}] {url}");
    }
    catch (Exception ex) { Console.WriteLine($"  ERR [{label}] {ex.Message}"); }
}

// -- 6. Omnia: alternative PDF sources --------------------------------------------
Console.WriteLine();
Console.WriteLine("=== 6. Omnia alternative URLs ===");
var omniaAlts = new[]
{
    ("OM-PDL page",  "https://omniaindustries.com/product/pocket-door-lock/"),
    ("OM-PDL page2", "https://omniaindustries.com/products/pocket-door-locks/"),
    ("OM-PDL v1",    "https://omniaindustries.com/wp-content/uploads/omnia-7012-7035-7036-7037-7039-pocket-door-lock-installation-template.pdf"),
    ("OM-PDL v2",    "https://omniaindustries.com/wp-content/uploads/omnia-pocket-door-lock-installation-template.pdf"),
    ("OM-7035 v1",   "https://omniaindustries.com/wp-content/uploads/omnia-7035-7037-7039-pocket-door-lock-trim-specifications.pdf"),
    ("OM-7035 v2",   "https://omniaindustries.com/wp-content/uploads/omnia-pocket-door-lock-trim-specifications.pdf"),
    ("OM dl path",   "https://omniaindustries.com/downloads/omnia-7012-7035-7036-7037-7039-pocket-door-lock-installation-template.pdf"),
};
foreach (var (label, url) in omniaAlts)
{
    try
    {
        var (isPdf, code, ct, preview, _) = await FetchAsync(url, "https://omniaindustries.com/");
        var links = Regex.Matches(preview, @"href=[""']([^""']+\.pdf[^""']*)["" ']", RegexOptions.IgnoreCase);
        Console.WriteLine($"  {code} isPdf={isPdf} links={links.Count} [{label}] {url}");
        foreach (Match m in links) Console.WriteLine($"    -> {m.Groups[1].Value}");
    }
    catch (Exception ex) { Console.WriteLine($"  ERR [{label}] {ex.Message}"); }
}

Console.WriteLine();
Console.WriteLine("=== Done ===");
