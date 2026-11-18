using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.TemplateFinder;
using UglyToad.PdfPig;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Defaults for the "add template" forms: door material "Both", page counts read from the
/// template's PDF, and "all pages" as the Pages To Print when none is given.
/// </summary>
public static class TemplatePageInfo
{
    private static readonly HttpClient Http = TemplateFinderService.CreateHttpClient();

    /// <summary>The door material new templates default to ("Both"), or null if it's missing.</summary>
    /// <param name="materials">All door materials.</param>
    public static DoorMaterial? DefaultMaterial(IEnumerable<DoorMaterial> materials) =>
        materials.FirstOrDefault(m => string.Equals(m.Material, "Both", StringComparison.OrdinalIgnoreCase));

    /// <summary>The page range for all pages: "1" for one page, "1-N" otherwise.</summary>
    /// <param name="pageCount">The PDF's page count.</param>
    public static string AllPages(int pageCount) => pageCount <= 1 ? "1" : $"1-{pageCount}";

    /// <summary>How many pages a Pages To Print range selects, or null if it isn't valid yet.</summary>
    /// <param name="pagesToPrint">e.g. "1,3-5".</param>
    public static int? CountInRange(string? pagesToPrint)
    {
        if (string.IsNullOrWhiteSpace(pagesToPrint)) return null;
        try
        {
            var pages = new PageRangeParser().Parse(pagesToPrint.Trim());
            return pages.Count > 0 ? pages.Count : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Counts the pages of a template's PDF: the local file if it exists, otherwise the online
    /// link (downloaded). Returns null when neither can be read as a PDF.
    /// </summary>
    /// <param name="localPath">Local file path, if any.</param>
    /// <param name="onlineLink">Online link, if any.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    public static async Task<int?> CountPagesAsync(string? localPath, string? onlineLink, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath.Trim()))
                return Count(await File.ReadAllBytesAsync(localPath.Trim(), cancellationToken).ConfigureAwait(false));

            if (!string.IsNullOrWhiteSpace(onlineLink) &&
                Uri.TryCreate(onlineLink.Trim(), UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                TemplateFinderService.AddBrowserHeaders(request, accept: "application/pdf,*/*;q=0.8");
                using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                return Count(await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // unreadable → null
        }
        return null;
    }

    private static int? Count(byte[] bytes)
    {
        if (!TemplateLinkChecker.LooksLikePdf(bytes)) return null;
        using var doc = PdfDocument.Open(bytes);
        return doc.NumberOfPages;
    }
}
