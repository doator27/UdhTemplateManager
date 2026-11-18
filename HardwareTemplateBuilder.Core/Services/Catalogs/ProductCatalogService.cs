using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services.Import;
using HardwareTemplateBuilder.Core.Services.TemplateFinder;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace HardwareTemplateBuilder.Core.Services.Catalogs;

/// <summary>
/// Manages product catalogs (Maintenance → Product Catalogs): adds a catalog from a local PDF or
/// an online link, downloads online catalogs into the template storage location
/// (<c>Product Catalogs/{Manufacturer}/</c>), reads every page's text into
/// <see cref="ProductCatalogPage"/> rows (scanned pages with text recognition when available),
/// and cuts single pages out for previews.
/// </summary>
public sealed class ProductCatalogService
{
    /// <summary>Largest catalog downloaded.</summary>
    public const long MaxDownloadBytes = 400L * 1024 * 1024;

    /// <summary>Folder (under the template storage location) where downloaded catalogs are kept.</summary>
    public const string FolderName = "Product Catalogs";

    private readonly Func<AppDbContext> _contextFactory;
    private readonly HttpClient _http;

    /// <summary>Creates the service.</summary>
    /// <param name="contextFactory">Creates database contexts.</param>
    /// <param name="http">HTTP client for downloads (see <see cref="TemplateFinderService.CreateHttpClient"/>).</param>
    public ProductCatalogService(Func<AppDbContext> contextFactory, HttpClient http)
    {
        _contextFactory = contextFactory;
        _http = http;
    }

    /// <summary>A manufacturer's catalogs, by title.</summary>
    /// <param name="manufacturerId">The manufacturer.</param>
    public List<ProductCatalog> ForManufacturer(int manufacturerId)
    {
        using var ctx = _contextFactory();
        return ctx.ProductCatalogs.AsNoTracking()
            .Where(c => c.ManufacturerId == manufacturerId)
            .OrderBy(c => c.Title)
            .ToList();
    }

    /// <summary>
    /// Adds a catalog (not read yet; call <see cref="IndexAsync"/>). Returns the existing one when
    /// the manufacturer already has a catalog with the same link or file.
    /// </summary>
    /// <param name="manufacturerId">The manufacturer.</param>
    /// <param name="title">Title (blank = the file name).</param>
    /// <param name="onlineLink">Online link, if any.</param>
    /// <param name="localPath">Local file, if any.</param>
    public ProductCatalog Add(int manufacturerId, string? title, string? onlineLink, string? localPath)
    {
        onlineLink = string.IsNullOrWhiteSpace(onlineLink) ? null : onlineLink.Trim();
        localPath = string.IsNullOrWhiteSpace(localPath) ? null : localPath.Trim().Trim('"');
        if (onlineLink == null && localPath == null) throw new ArgumentException("Give a catalog file or link.");

        using var ctx = _contextFactory();
        var existing = ctx.ProductCatalogs.FirstOrDefault(c => c.ManufacturerId == manufacturerId &&
            ((onlineLink != null && c.OnlineLink == onlineLink) || (localPath != null && c.LocalLink == localPath)));
        if (existing != null) return existing;

        var catalog = new ProductCatalog
        {
            ManufacturerId = manufacturerId,
            Title = Truncate(string.IsNullOrWhiteSpace(title) ? TitleFromLink(localPath ?? onlineLink!) : title.Trim(), 300),
            OnlineLink = onlineLink,
            LocalLink = localPath,
            AddedUtc = DateTime.UtcNow,
        };
        ctx.ProductCatalogs.Add(catalog);
        ctx.SaveChanges();
        return catalog;
    }

    /// <summary>Renames a catalog.</summary>
    /// <param name="catalogId">The catalog.</param>
    /// <param name="title">The new title.</param>
    public void Rename(int catalogId, string title)
    {
        using var ctx = _contextFactory();
        var c = ctx.ProductCatalogs.Find(catalogId) ?? throw new InvalidOperationException("That catalog no longer exists.");
        c.Title = Truncate(title.Trim(), 300);
        ctx.SaveChanges();
    }

    /// <summary>Removes a catalog and its page text (a downloaded copy is deleted too; your own files are left alone).</summary>
    /// <param name="catalogId">The catalog.</param>
    public void Remove(int catalogId)
    {
        using var ctx = _contextFactory();
        var c = ctx.ProductCatalogs.Find(catalogId);
        if (c == null) return;
        var file = c.OnlineLink != null ? c.LocalLink : null;
        ctx.ProductCatalogPages.RemoveRange(ctx.ProductCatalogPages.Where(p => p.CatalogId == catalogId));
        ctx.ProductCatalogs.Remove(c);
        ctx.SaveChanges();
        try
        {
            if (file != null && File.Exists(file) && IsInStorage(ctx, file)) File.Delete(file);
        }
        catch (IOException) { /* in use: leave it */ }
        catch (UnauthorizedAccessException) { /* leave it */ }
    }

    /// <summary>
    /// Reads the catalog's text into the database (replacing what was read before), downloading
    /// it first when it only has an online link (or its local copy is gone). Returns the page count.
    /// Failures are saved on the catalog (<see cref="ProductCatalog.LastError"/>) and rethrown.
    /// </summary>
    /// <param name="catalogId">The catalog.</param>
    /// <param name="ocr">Text recognition for scanned pages, or null.</param>
    /// <param name="progress">Status text.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async Task<int> IndexAsync(int catalogId, IPdfOcrEngine? ocr, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var path = await EnsureLocalFileAsync(catalogId, progress, cancellationToken).ConfigureAwait(false);
            var text = await HardwarePdfTextReader.ReadAsync(path, ocr, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report("Saving the catalog text...");
            using var ctx = _contextFactory();
            var catalog = ctx.ProductCatalogs.Find(catalogId) ?? throw new InvalidOperationException("That catalog no longer exists.");
            ctx.ProductCatalogPages.RemoveRange(ctx.ProductCatalogPages.Where(p => p.CatalogId == catalogId));
            ctx.SaveChanges();

            for (int i = 0; i < text.Pages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageText = PageText(text.Pages[i]);
                ctx.ProductCatalogPages.Add(new ProductCatalogPage
                {
                    CatalogId = catalogId,
                    PageNumber = i + 1,
                    Text = pageText,
                    SearchKey = ImportText.Key(pageText),
                });
                if ((i + 1) % 50 == 0)
                {
                    ctx.SaveChanges();
                    ctx.ChangeTracker.Clear();
                    progress?.Report($"Saving the catalog text ({i + 1} of {text.Pages.Count} pages)...");
                }
            }
            ctx.SaveChanges();
            ctx.ChangeTracker.Clear();

            catalog = ctx.ProductCatalogs.Find(catalogId)!;
            catalog.PageCount = text.Pages.Count;
            catalog.IndexedUtc = DateTime.UtcNow;
            catalog.LastError = text.UnreadablePages.Count > 0
                ? Truncate($"{text.UnreadablePages.Count} scanned page(s) couldn't be read (no text recognition on this computer).", 1000)
                : null;
            ctx.SaveChanges();
            return text.Pages.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                using var ctx = _contextFactory();
                var c = ctx.ProductCatalogs.Find(catalogId);
                if (c != null)
                {
                    c.LastError = Truncate(ex.Message, 1000);
                    ctx.SaveChanges();
                }
            }
            catch
            {
                // keep the original error
            }
            throw;
        }
    }

    /// <summary>
    /// The catalog's local file: its own file when it exists, else a download of its online link
    /// into the template storage location (saved as the catalog's local link).
    /// </summary>
    /// <param name="catalogId">The catalog.</param>
    /// <param name="progress">Status text.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    public async Task<string> EnsureLocalFileAsync(int catalogId, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        string? online;
        string folder;
        string title;
        using (var ctx = _contextFactory())
        {
            var c = ctx.ProductCatalogs.AsNoTracking().FirstOrDefault(x => x.Id == catalogId)
                    ?? throw new InvalidOperationException("That catalog no longer exists.");
            if (!string.IsNullOrWhiteSpace(c.LocalLink) && File.Exists(c.LocalLink)) return c.LocalLink!;
            online = c.OnlineLink;
            if (string.IsNullOrWhiteSpace(online))
                throw new FileNotFoundException($"The catalog file can't be found: {c.LocalLink}");
            var mfr = ctx.Manufacturers.AsNoTracking().FirstOrDefault(m => m.Id == c.ManufacturerId)?.ManufacturerName ?? "Unknown";
            folder = Path.Combine(StorageRoot(ctx), FolderName, SafeName(mfr));
            title = c.Title;
        }

        Directory.CreateDirectory(folder);
        var fileName = SafeName(Path.GetFileNameWithoutExtension(FileNameFromLink(online!)) is { Length: > 0 } n ? n : title) + ".pdf";
        var path = Path.Combine(folder, fileName);
        var temp = path + ".download";

        progress?.Report("Downloading the catalog...");
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, online);
            TemplateFinderService.AddBrowserHeaders(request, accept: "application/pdf,*/*;q=0.8");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Downloading the catalog failed (HTTP {(int)response.StatusCode}).");
            var total = response.Content.Headers.ContentLength;
            if (total > MaxDownloadBytes) throw new InvalidOperationException("The catalog is too large to download.");

            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var target = File.Create(temp))
            {
                var buffer = new byte[81920];
                long done = 0, lastReport = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    done += read;
                    if (done > MaxDownloadBytes) throw new InvalidOperationException("The catalog is too large to download.");
                    if (done - lastReport > 2 * 1024 * 1024)
                    {
                        lastReport = done;
                        progress?.Report(total is > 0
                            ? $"Downloading the catalog ({done * 100 / total.Value}%)..."
                            : $"Downloading the catalog ({done / (1024 * 1024)} MB)...");
                    }
                }
            }
        }

        var head = new byte[1024];
        int got;
        using (var check = File.OpenRead(temp)) got = check.Read(head, 0, head.Length);
        if (!TemplateLinkChecker.LooksLikePdf(head.Take(got).ToArray()))
        {
            File.Delete(temp);
            throw new InvalidOperationException("That link didn't download a PDF (the site may need a browser, or the link is a web page).");
        }
        File.Move(temp, path, overwrite: true);

        using (var ctx = _contextFactory())
        {
            var c = ctx.ProductCatalogs.Find(catalogId);
            if (c != null)
            {
                c.LocalLink = path;
                ctx.SaveChanges();
            }
        }
        return path;
    }

    /// <summary>One page of a catalog as a single-page PDF, for the preview.</summary>
    /// <param name="catalogId">The catalog.</param>
    /// <param name="pageNumber">The 1-based page.</param>
    /// <param name="cancellationToken">Cancels a download.</param>
    public async Task<byte[]> PagePdfAsync(int catalogId, int pageNumber, CancellationToken cancellationToken)
    {
        var path = await EnsureLocalFileAsync(catalogId, null, cancellationToken).ConfigureAwait(false);
        return ExtractPage(path, pageNumber);
    }

    /// <summary>Cuts one page out of a PDF.</summary>
    /// <param name="path">The PDF.</param>
    /// <param name="pageNumber">The 1-based page.</param>
    public static byte[] ExtractPage(string path, int pageNumber)
    {
        using var source = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        if (pageNumber < 1 || pageNumber > source.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), $"The catalog has {source.PageCount} pages.");
        using var output = new PdfDocument();
        output.AddPage(source.Pages[pageNumber - 1]);
        using var ms = new MemoryStream();
        output.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>List text for a catalog: "LCN Price Book 2026  (212 pages, read 11/18/2026)".</summary>
    /// <param name="c">The catalog.</param>
    public static string DisplayText(ProductCatalog c)
    {
        string state = c.IndexedUtc is DateTime read
            ? $"{c.PageCount} page{(c.PageCount == 1 ? "" : "s")}, read {read.ToLocalTime():d}"
            : "text not read yet";
        if (!string.IsNullOrEmpty(c.LastError)) state += $" - {c.LastError}";
        var where = !string.IsNullOrWhiteSpace(c.OnlineLink) ? "online" : "file";
        return $"{c.Title}  ({state}; {where})";
    }

    /// <summary>The Google search to open in the browser for a manufacturer's catalogs.</summary>
    /// <param name="manufacturerName">The manufacturer.</param>
    public static string GoogleSearchUrl(string manufacturerName) =>
        "https://www.google.com/search?q=" + Uri.EscapeDataString($"{manufacturerName} product catalog price book pdf");

    /// <summary>Joins a page's layout lines, collapsing long runs of spaces to two (still a column gap).</summary>
    /// <param name="lines">Layout lines.</param>
    public static string PageText(IEnumerable<string> lines) =>
        string.Join("\n", lines.Select(l => Regex.Replace(l.TrimEnd(), @" {3,}", "  ")).Where(l => l.Trim().Length > 0));

    /// <summary>A title from a file name or link: "LCN_Price_Book_2026.pdf" → "LCN Price Book 2026".</summary>
    /// <param name="link">File path or URL.</param>
    public static string TitleFromLink(string link)
    {
        var name = Path.GetFileNameWithoutExtension(FileNameFromLink(link));
        name = Regex.Replace(name, @"[_+]+|%20", " ");
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return name.Length > 0 ? name : link;
    }

    private static string FileNameFromLink(string link)
    {
        if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var path = uri.AbsolutePath;
            return Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
        }
        return Path.GetFileName(link);
    }

    private static string StorageRoot(AppDbContext ctx)
    {
        var configured = new AppSettingRepository(ctx).GetValue("TemplateStorageLocation");
        return !string.IsNullOrWhiteSpace(configured) ? configured : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static bool IsInStorage(AppDbContext ctx, string file)
    {
        var root = Path.GetFullPath(Path.Combine(StorageRoot(ctx), FolderName));
        return Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var s = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim(' ', '.');
        return s.Length == 0 ? "catalog" : s.Length > 100 ? s[..100] : s;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
