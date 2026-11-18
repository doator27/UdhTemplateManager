using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services.Import;

namespace HardwareTemplateBuilder.Core.Services.TemplateFinder;

/// <summary>
/// Looks for template PDFs online without any paid service:
/// <list type="number">
/// <item><b>Broken link fixes</b>: the same link over https/http and the Internet Archive's saved copy.</item>
/// <item><b>Your past links</b>: links of this manufacturer's other templates in the database whose file
/// name contains their template/model number, with that number swapped for the one being searched
/// (e.g. <c>ngp.com/…/613_cutsheet.pdf</c> → <c>…/5050C_cutsheet.pdf</c>).</item>
/// <item><b>Manufacturer website</b>: the sites this manufacturer's past links point to (or a guess
/// from its name) are crawled through their site maps for PDF links and product pages that mention
/// the model/template number or past labels; product pages are read for PDF links.</item>
/// </list>
/// Every link is downloaded and checked (<see cref="TemplateLinkChecker"/>); only real PDFs are
/// offered, best first (mentions the number, looks like a template).
/// </summary>
public sealed class TemplateFinderService
{
    /// <summary>Hosts that serve files for many companies, so they aren't crawled as "the manufacturer's site".</summary>
    private static readonly string[] SharedHosts =
    {
        "storage.googleapis.com", "cloudinary.com", "cloudfront.net", "hubspotusercontent", "mediavalet.com",
        "amazonaws.com", "web.archive.org", "dropbox", "sharepoint", "box.com", "azureedge.net",
    };

    private const int MaxSitemapFiles = 12;
    private const int MaxPagesToRead = 10;
    private const int MaxLinksToCheck = 24;

    private readonly HttpClient _http;
    private readonly Func<AppDbContext> _contextFactory;
    private readonly TemplateLinkChecker _checker;
    private readonly ConcurrentDictionary<string, bool> _blockedSites = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the websites that refused the last search (bot protection such as Cloudflare's
    /// "Just a moment..." page, or robots.txt rules), so the window can suggest searching them in a browser.
    /// </summary>
    public IReadOnlyCollection<string> BlockedSites => _blockedSites.Keys.ToList();

    /// <summary>Creates the service.</summary>
    /// <param name="http">HTTP client (see <see cref="CreateHttpClient"/>).</param>
    /// <param name="contextFactory">Creates database contexts (for link history).</param>
    public TemplateFinderService(HttpClient http, Func<AppDbContext> contextFactory)
    {
        _http = http;
        _contextFactory = contextFactory;
        _checker = new TemplateLinkChecker(http);
    }

    /// <summary>An HTTP client set up like a browser (redirects, compression, cookies).</summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>Adds the headers a desktop browser sends (some manufacturer sites refuse bare clients).</summary>
    /// <param name="request">The request.</param>
    /// <param name="accept">Accept header value.</param>
    public static void AddBrowserHeaders(HttpRequestMessage request, string accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8")
    {
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
    }

    /// <summary>Checks a single link the user pasted.</summary>
    /// <param name="url">The link.</param>
    /// <param name="request">The search (for its terms).</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    public Task<TemplateCandidate> CheckUrlAsync(string url, TemplateSearchRequest request, CancellationToken cancellationToken) =>
        _checker.CheckAsync(url.Trim(), "Pasted link", request.Terms(), cancellationToken);

    /// <summary>
    /// Runs every strategy, checks the links it finds, and returns the real PDFs best first.
    /// <paramref name="onFound"/> is called for each PDF as soon as it's checked, so a window can
    /// show results while the search continues.
    /// </summary>
    /// <param name="request">What to look for.</param>
    /// <param name="progress">Status text.</param>
    /// <param name="onFound">Called for each PDF found (any thread).</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    public async Task<List<TemplateCandidate>> FindAsync(
        TemplateSearchRequest request, IProgress<string>? progress, Action<TemplateCandidate>? onFound, CancellationToken cancellationToken)
    {
        _blockedSites.Clear();
        var terms = request.Terms();
        var found = new ConcurrentDictionary<string, TemplateCandidate>(StringComparer.OrdinalIgnoreCase);
        var tried = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        async Task CheckAll(IEnumerable<(string Url, string Source, int Bonus)> links, string status)
        {
            var batch = links.Where(l => IsHttp(l.Url) && tried.TryAdd(Normalize(l.Url), true)).Take(MaxLinksToCheck).ToList();
            if (batch.Count == 0) return;
            progress?.Report($"{status} (checking {batch.Count} link{(batch.Count == 1 ? "" : "s")})...");
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(batch.Select(async link =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var c = await _checker.CheckAsync(link.Url, link.Source, terms, cancellationToken).ConfigureAwait(false);
                    if (!c.IsPdf) return;
                    c.Score += link.Bonus;
                    if (found.TryAdd(Normalize(c.Url), c)) onFound?.Invoke(c);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
        }

        // 1. Broken link: same link again, other scheme, archived copy.
        if (!string.IsNullOrWhiteSpace(request.BrokenLink) && IsHttp(request.BrokenLink))
        {
            var broken = request.BrokenLink!.Trim();
            var links = new List<(string, string, int)>
            {
                (broken, "Old link (works again?)", 40),
                (SwapScheme(broken), "Old link over " + (broken.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "http" : "https"), 30),
            };
            progress?.Report("Looking for a saved copy of the old link...");
            var archived = await ArchivedCopyAsync(broken, cancellationToken).ConfigureAwait(false);
            if (archived != null) links.Add((archived, "Internet Archive copy of the old link", 20));
            await CheckAll(links, "Trying the old link").ConfigureAwait(false);
        }

        // 1b. Templates already in the database under a number the product catalog gives.
        if (request.CatalogTemplateNumbers.Count > 0)
        {
            var keys = request.CatalogTemplateNumbers.Select(ImportText.Key).Where(k => k.Length > 0).ToHashSet();
            List<(string Number, string Link)> mine;
            using (var ctx = _contextFactory())
            {
                mine = ctx.IndividualTemplates
                    .Where(t => t.ManufacturerId == request.ManufacturerId && t.OnlineLink != null && t.OnlineLink != "")
                    .Select(t => new { t.TemplateNumber, t.OnlineLink })
                    .ToList()
                    .Where(t => keys.Contains(ImportText.Key(t.TemplateNumber)))
                    .Select(t => (t.TemplateNumber, t.OnlineLink!))
                    .ToList();
            }
            await CheckAll(mine.Select(m => (m.Link, $"Your template {m.Number} (from the catalog)", 60)), "Trying your templates the catalog names").ConfigureAwait(false);
        }

        // 2. Patterns from this manufacturer's past links.
        List<(string Number, string Link)> history;
        using (var ctx = _contextFactory())
        {
            history = ctx.IndividualTemplates
                .Where(t => t.ManufacturerId == request.ManufacturerId && t.OnlineLink != null && t.OnlineLink != "")
                .Select(t => new { t.TemplateNumber, t.OnlineLink })
                .ToList()
                .Select(t => (t.TemplateNumber, t.OnlineLink!))
                .ToList();
        }
        await CheckAll(PatternLinks(history, terms).Select(u => (u, "Your past links (same pattern)", 30)), "Trying your past link patterns").ConfigureAwait(false);

        // 3. Crawl the manufacturer's website(s).
        foreach (var site in Sites(request.ManufacturerName, history.Select(h => h.Link)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Searching {site}...");
            try
            {
                var keys = terms.Select(ImportText.Key).Where(k => k.Length >= 3).Distinct().ToList();
                var links = await CrawlSiteAsync(site, url => UrlScore(url, keys), MapScore, progress, cancellationToken).ConfigureAwait(false);
                await CheckAll(links.Select(l => (l.Url, $"{site} website", l.Score)), $"Searching {site}").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // site too slow: move on
            }
            catch (HttpRequestException)
            {
                // site unreachable: move on
            }
        }

        progress?.Report(found.IsEmpty ? "No template PDFs found." : $"Found {found.Count} PDF(s).");
        return found.Values.OrderByDescending(c => c.Score).ToList();
    }

    /// <summary>The manufacturer websites a search would crawl (from past links, else a name guess).</summary>
    /// <param name="request">The search.</param>
    public List<string> SitesFor(TemplateSearchRequest request)
    {
        using var ctx = _contextFactory();
        var links = ctx.IndividualTemplates
            .Where(t => t.ManufacturerId == request.ManufacturerId && t.OnlineLink != null && t.OnlineLink != "")
            .Select(t => t.OnlineLink!)
            .ToList();
        return Sites(request.ManufacturerName, links);
    }

    // ── Past-link patterns ──────────────────────────────────────────────────

    /// <summary>
    /// For each past link whose file name contains its own template number, swaps that number for
    /// each search term (keeping the rest of the file name), plus the same with the "(n)" copy
    /// number some sites add removed.
    /// </summary>
    /// <param name="history">This manufacturer's (template number, link) pairs.</param>
    /// <param name="terms">Search terms.</param>
    public static List<string> PatternLinks(IEnumerable<(string Number, string Link)> history, IReadOnlyList<string> terms)
    {
        var result = new List<string>();
        var seenPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (number, link) in history)
        {
            if (string.IsNullOrWhiteSpace(number) || !Uri.TryCreate(link, UriKind.Absolute, out var uri)) continue;
            var path = uri.AbsolutePath;
            var slash = path.LastIndexOf('/');
            var fileName = Uri.UnescapeDataString(path[(slash + 1)..]);
            var token = number.Trim();
            int at = fileName.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (at < 0 || token.Length < 2) continue;

            var pattern = fileName[..at] + "{0}" + fileName[(at + token.Length)..];
            var withoutCopy = Regex.Replace(pattern, @"\(\d+\)(?=\.pdf$)", string.Empty, RegexOptions.IgnoreCase);
            foreach (var p in new[] { pattern, withoutCopy }.Distinct())
            {
                var prefix = uri.GetLeftPart(UriPartial.Authority) + path[..(slash + 1)];
                if (!seenPatterns.Add(prefix + p)) continue;
                foreach (var term in terms.Take(4))
                {
                    var name = string.Format(p, term.Trim());
                    result.Add(prefix + Uri.EscapeDataString(name).Replace("%28", "(").Replace("%29", ")"));
                }
            }
            if (seenPatterns.Count > 12) break;
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── Manufacturer website crawl ──────────────────────────────────────────

    /// <summary>
    /// The websites to crawl: hosts this manufacturer's past links point to (most used first,
    /// shared file hosts excluded), else a guess from the name ("www.{name}.com").
    /// </summary>
    /// <param name="manufacturerName">Manufacturer name.</param>
    /// <param name="pastLinks">This manufacturer's past links.</param>
    public static List<string> Sites(string manufacturerName, IEnumerable<string> pastLinks)
    {
        var hosts = pastLinks
            .Select(l => Uri.TryCreate(l, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null)
            .Where(h => h != null && !SharedHosts.Any(s => h!.Contains(s, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(h => h!)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(3)
            .ToList();
        if (hosts.Count == 0)
        {
            var slug = Regex.Replace(manufacturerName.ToLowerInvariant(), @"\(.*?\)|[^a-z0-9]", string.Empty);
            if (slug.Length >= 3) hosts.Add($"www.{slug}.com");
        }
        return hosts;
    }

    /// <summary>
    /// Walks a site's site maps (robots.txt rules obeyed) and the best-scoring pages they list,
    /// and returns the PDF links found with their scores. <paramref name="urlScore"/> scores PDF
    /// and page URLs (0 = not interesting); <paramref name="mapScore"/> orders child site maps.
    /// </summary>
    private async Task<List<(string Url, int Score)>> CrawlSiteAsync(
        string host, Func<string, int> urlScore, Func<string, int> mapScore, IProgress<string>? progress, CancellationToken ct)
    {
        var results = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pages = new List<(string Url, int Score)>();

        // Site maps: robots.txt "Sitemap:" lines, else the usual names.
        var queue = new Queue<string>();
        var robots = await GetTextAsync($"https://{host}/robots.txt", ct).ConfigureAwait(false);
        var rules = RobotsRules.Parse(robots);
        if (_blockedSites.ContainsKey(host)) return new List<(string, int)>();
        if (!rules.IsAllowed("/"))
        {
            _blockedSites.TryAdd(host, true);
            return new List<(string, int)>();
        }
        foreach (Match m in Regex.Matches(robots ?? string.Empty, @"(?im)^\s*Sitemap:\s*(\S+)"))
            queue.Enqueue(m.Groups[1].Value.Trim());
        if (queue.Count == 0)
        {
            queue.Enqueue($"https://{host}/sitemap.xml");
            queue.Enqueue($"https://{host}/sitemap_index.xml");
        }

        int fetched = 0;
        var seenMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var childMaps = new List<(string Url, int Score)>();
        while ((queue.Count > 0 || childMaps.Count > 0) && fetched < MaxSitemapFiles)
        {
            ct.ThrowIfCancellationRequested();
            string map;
            if (queue.Count > 0) map = queue.Dequeue();
            else
            {
                var best = childMaps.OrderByDescending(c => c.Score).First();
                childMaps.Remove(best);
                map = best.Url;
            }
            if (!seenMaps.Add(map) || !rules.IsAllowed(map)) continue;
            fetched++;
            progress?.Report($"Searching {host} (site map {fetched})...");
            var xml = await GetTextAsync(map, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(xml)) continue;

            foreach (Match m in Regex.Matches(xml, @"<loc>\s*(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?\s*</loc>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var url = WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
                if (!IsHttp(url) || !rules.IsAllowed(url)) continue;
                var lower = url.ToLowerInvariant();
                bool isMap = xml.Contains("<sitemapindex", StringComparison.OrdinalIgnoreCase) || lower.EndsWith(".xml") || lower.EndsWith(".xml.gz");
                if (isMap)
                {
                    childMaps.Add((url, mapScore(lower)));
                    continue;
                }
                int score = urlScore(url);
                if (score <= 0) continue;
                if (lower.Contains(".pdf")) results[url] = Math.Max(results.GetValueOrDefault(url), score + 20);
                else pages.Add((url, score));
            }
        }

        // Product pages that mention the number: read them for PDF links.
        foreach (var page in pages.Where(p => rules.IsAllowed(p.Url)).OrderByDescending(p => p.Score).DistinctBy(p => p.Url).Take(MaxPagesToRead))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Reading {host} page {Shorten(page.Url)}...");
            var html = await GetTextAsync(page.Url, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(html)) continue;
            foreach (var link in PdfLinks(html, page.Url).Where(l => !SameHost(l, host) || rules.IsAllowed(l)))
            {
                int score = urlScore(link) + page.Score / 2 + 5;
                results[link] = Math.Max(results.GetValueOrDefault(link), score);
            }
        }
        return results.Select(r => (r.Key, r.Value)).OrderByDescending(r => r.Value).ToList();
    }

    private static int MapScore(string lowerUrl) =>
        Regex.IsMatch(lowerUrl, "pdf|document|asset|media|file|literature|resource|product|download") ? 10 : 1;

    // ── Product catalogs ────────────────────────────────────────────────────

    /// <summary>
    /// Looks for product catalog PDFs (catalogs, price books, product guides) on the
    /// manufacturer's website(s), the same way the template search crawls them. Links are not
    /// downloaded: the user picks which ones to add. Sites with bot protection end up in
    /// <see cref="BlockedSites"/>.
    /// </summary>
    /// <param name="manufacturerId">The manufacturer (its past template links tell which sites to search).</param>
    /// <param name="manufacturerName">Its name (used to guess the site when there are no past links).</param>
    /// <param name="progress">Status text.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    public async Task<List<(string Url, int Score)>> FindCatalogLinksAsync(
        int manufacturerId, string manufacturerName, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        _blockedSites.Clear();
        var sites = SitesFor(new TemplateSearchRequest { ManufacturerId = manufacturerId, ManufacturerName = manufacturerName });
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Searching {site} for catalogs...");
            try
            {
                foreach (var (url, score) in await CrawlSiteAsync(site, CatalogUrlScore, CatalogMapScore, progress, cancellationToken).ConfigureAwait(false))
                {
                    if (!url.Contains(".pdf", StringComparison.OrdinalIgnoreCase) || CatalogUrlScore(url) <= 0) continue;
                    found[url] = Math.Max(found.GetValueOrDefault(url), score);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // site too slow: move on
            }
            catch (HttpRequestException)
            {
                // site unreachable: move on
            }
        }
        progress?.Report(found.Count == 0 ? "No catalogs found." : $"Found {found.Count} catalog link(s).");
        return found.Select(f => (f.Key, f.Value)).OrderByDescending(f => f.Item2).ThenBy(f => f.Item1).ToList();
    }

    /// <summary>Scores a URL as a product catalog: "catalog", "price book", "product guide" and similar; 0 = not a catalog.</summary>
    /// <param name="url">The URL.</param>
    public static int CatalogUrlScore(string url)
    {
        var lower = Uri.UnescapeDataString(url).ToLowerInvariant();
        int score = 0;
        if (Regex.IsMatch(lower, "catalog|catalogue")) score += 30;
        if (Regex.IsMatch(lower, @"price[\s_\-]?(book|list)|pricebook")) score += 25;
        if (Regex.IsMatch(lower, @"product[\s_\-]?(guide|line|selection)|specifier|selection[\s_\-]?guide|ordering[\s_\-]?(guide|info)")) score += 15;
        if (score == 0)
        {
            // Pages that usually link to catalogs (read for their PDF links, never added themselves).
            return lower.Contains(".pdf") ? 0 : Regex.IsMatch(lower, "literature|catalog|download|resource|document|library") ? 8 : 0;
        }
        if (Regex.IsMatch(lower, "template|install|instruction|warranty|certificate|declaration|msds|sds")) score -= 20;
        return Math.Max(score, 1);
    }

    private static int CatalogMapScore(string lowerUrl) =>
        Regex.IsMatch(lowerUrl, "pdf|document|asset|media|file|literature|resource|download|catalog") ? 10 : 1;

    /// <summary>Scores a URL: contains a search term (longer = better), mentions template/instructions.</summary>
    /// <param name="url">The URL.</param>
    /// <param name="keys">Comparison keys of the search terms.</param>
    public static int UrlScore(string url, IReadOnlyList<string> keys)
    {
        var key = ImportText.Key(Uri.UnescapeDataString(url));
        int best = 0;
        foreach (var k in keys)
        {
            int at = key.IndexOf(k, StringComparison.Ordinal);
            if (at < 0) continue;
            // A number glued to more digits ("613" inside "26131") is a weak match.
            bool digitBefore = at > 0 && char.IsDigit(key[at - 1]) && char.IsDigit(k[0]);
            bool digitAfter = at + k.Length < key.Length && char.IsDigit(key[at + k.Length]) && char.IsDigit(k[^1]);
            best = Math.Max(best, k.Length * (digitBefore || digitAfter ? 2 : 10));
        }
        if (best == 0) return 0;
        var lower = url.ToLowerInvariant();
        if (Regex.IsMatch(lower, "template|install|instruction|cutsheet|cut-sheet|prep")) best += 15;
        if (Regex.IsMatch(lower, "brochure|catalog|price|warranty|certificate|declaration")) best -= 10;
        return Math.Max(best, 1);
    }

    /// <summary>The PDF links on an HTML page, made absolute.</summary>
    /// <param name="html">Page HTML.</param>
    /// <param name="pageUrl">The page's own URL.</param>
    public static List<string> PdfLinks(string html, string pageUrl)
    {
        var result = new List<string>();
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri)) return result;
        foreach (Match m in Regex.Matches(html, @"(?:href|data-href|data-url|src)\s*=\s*[""']([^""'#]+?\.pdf(?:\?[^""'#]*)?)[""']", RegexOptions.IgnoreCase))
        {
            var raw = WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
            if (Uri.TryCreate(baseUri, raw, out var abs) && IsHttp(abs.ToString())) result.Add(abs.ToString());
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── Internet Archive ────────────────────────────────────────────────────

    private async Task<string?> ArchivedCopyAsync(string url, CancellationToken ct)
    {
        var json = await GetTextAsync("https://archive.org/wayback/available?url=" + Uri.EscapeDataString(url), ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("archived_snapshots", out var snaps) &&
                snaps.TryGetProperty("closest", out var closest) &&
                closest.TryGetProperty("available", out var available) && available.GetBoolean() &&
                closest.TryGetProperty("timestamp", out var ts))
            {
                // "id_" asks for the original file without the archive's page wrapper.
                return $"https://web.archive.org/web/{ts.GetString()}id_/{url}";
            }
        }
        catch (JsonException)
        {
            // no copy
        }
        return null;
    }

    // ── HTTP helpers ────────────────────────────────────────────────────────

    private async Task<string?> GetTextAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddBrowserHeaders(request);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Bot protection (Cloudflare "Just a moment...", Akamai, ...): remember the site.
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable or (HttpStatusCode)429 &&
                    !url.Contains("archive.org", StringComparison.OrdinalIgnoreCase))
                {
                    var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    if (Regex.IsMatch(body, "Just a moment|cf-challenge|challenge-platform|Access Denied|captcha", RegexOptions.IgnoreCase) &&
                        Uri.TryCreate(url, UriKind.Absolute, out var u))
                        _blockedSites.TryAdd(u.Host.ToLowerInvariant(), true);
                }
                return null;
            }
            if (response.Content.Headers.ContentLength is > 20 * 1024 * 1024) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
            {
                using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var reader = new StreamReader(gz);
                return await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            }
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static bool SameHost(string url, string host) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase);

    private static bool IsHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static string Normalize(string url) => Regex.Replace(url.Trim(), "^https?://", string.Empty, RegexOptions.IgnoreCase).TrimEnd('/');

    private static string SwapScheme(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "http://" + url[8..]
        : url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[7..] : url;

    private static string Shorten(string url) => url.Length <= 60 ? url : "..." + url[^57..];
}
