using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Services.Import;
using UglyToad.PdfPig;

namespace HardwareTemplateBuilder.Core.Services.TemplateFinder;

/// <summary>
/// Downloads a link and checks it: is it really a PDF, how many pages, and does its text mention
/// one of the search terms or look like a template/installation sheet.
/// </summary>
public sealed class TemplateLinkChecker
{
    /// <summary>Largest PDF downloaded for checking/preview.</summary>
    public const int MaxBytes = 40 * 1024 * 1024;

    private static readonly string[] TemplateWords = { "TEMPLATE", "INSTALLATION", "INSTRUCTIONS", "CUTOUT", "DOOR PREP", "MOUNTING", "CUT SHEET", "CUTSHEET" };
    private readonly HttpClient _http;

    /// <summary>Creates a checker using the given client (see <see cref="TemplateFinderService.CreateHttpClient"/>).</summary>
    /// <param name="http">HTTP client.</param>
    public TemplateLinkChecker(HttpClient http) => _http = http;

    /// <summary>Downloads and checks one link.</summary>
    /// <param name="url">The link.</param>
    /// <param name="source">Where it came from.</param>
    /// <param name="terms">Search terms to look for in the PDF text.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    public async Task<TemplateCandidate> CheckAsync(string url, string source, IReadOnlyList<string> terms, CancellationToken cancellationToken)
    {
        var candidate = new TemplateCandidate { Url = url, Source = source };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            TemplateFinderService.AddBrowserHeaders(request, accept: "application/pdf,*/*;q=0.8");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                candidate.Error = $"HTTP {(int)response.StatusCode}";
                return candidate;
            }
            if (response.Content.Headers.ContentLength is long length && length > MaxBytes)
            {
                candidate.Error = "too large";
                return candidate;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBytes) { candidate.Error = "too large"; return candidate; }
            }
            var bytes = buffer.ToArray();
            if (!LooksLikePdf(bytes))
            {
                candidate.Error = "not a PDF";
                return candidate;
            }

            candidate.IsPdf = true;
            candidate.PdfBytes = bytes;
            Inspect(candidate, bytes, terms);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            candidate.Error = "timed out";
        }
        catch (HttpRequestException ex)
        {
            candidate.Error = ex.StatusCode is { } code ? $"HTTP {(int)code}" : "couldn't connect";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            candidate.Error = ex.Message;
        }
        return candidate;
    }

    /// <summary>True when the bytes start with a PDF header (allowing a little leading junk).</summary>
    /// <param name="bytes">Downloaded bytes.</param>
    public static bool LooksLikePdf(byte[] bytes)
    {
        int limit = Math.Min(bytes.Length - 4, 1024);
        for (int i = 0; i <= limit; i++)
            if (bytes[i] == 0x25 && bytes[i + 1] == 0x50 && bytes[i + 2] == 0x44 && bytes[i + 3] == 0x46) return true;
        return false;
    }

    /// <summary>Reads page count and the first pages' text, and scores the candidate.</summary>
    /// <param name="candidate">The candidate to fill in.</param>
    /// <param name="bytes">The PDF.</param>
    /// <param name="terms">Search terms.</param>
    public static void Inspect(TemplateCandidate candidate, byte[] bytes, IReadOnlyList<string> terms)
    {
        string text = string.Empty;
        try
        {
            using var doc = PdfDocument.Open(bytes);
            candidate.PageCount = doc.NumberOfPages;
            text = string.Join(" ", doc.GetPages().Take(4).Select(p => p.Text));
        }
        catch
        {
            candidate.PageCount = Math.Max(candidate.PageCount, 1);
        }

        var textKey = ImportText.Key(text);
        var urlKey = ImportText.Key(Uri.UnescapeDataString(candidate.Url));
        foreach (var term in terms)
        {
            var key = ImportText.Key(term);
            if (key.Length < 3) continue;
            if (textKey.Contains(key, StringComparison.Ordinal) || urlKey.Contains(key, StringComparison.Ordinal))
            {
                candidate.MentionsTerm = true;
                candidate.MatchedTerm = term;
                break;
            }
        }
        var upper = text.ToUpperInvariant() + " " + candidate.Url.ToUpperInvariant();
        candidate.LooksLikeTemplate = TemplateWords.Any(w => upper.Contains(w, StringComparison.Ordinal));

        candidate.Score += 100;
        if (candidate.MentionsTerm) candidate.Score += 200;
        if (candidate.LooksLikeTemplate) candidate.Score += 50;
        if (candidate.PageCount is > 0 and <= 12) candidate.Score += 10;

        // The file name says what it is: templates/installation sheets first, ordering sheets,
        // manuals and brochures last.
        var file = Uri.UnescapeDataString(candidate.Url).ToLowerInvariant();
        file = file[(file.LastIndexOf('/') + 1)..];
        if (System.Text.RegularExpressions.Regex.IsMatch(file, "template|install|instruction|cut-?sheet|prep")) candidate.Score += 40;
        if (System.Text.RegularExpressions.Regex.IsMatch(file, "order|price|brochure|catalog|maintenance|parts|warranty|spec-?sheet|submittal")) candidate.Score -= 40;
    }
}
