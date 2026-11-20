using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>The outcome of analysing a hardware PDF.</summary>
public sealed class HardwarePdfAnalysis
{
    /// <summary>Gets the distinct callouts found, in document order.</summary>
    public List<HardwareImportGroup> Groups { get; init; } = new();

    /// <summary>Gets the number of hardware lines read (before de-duplication).</summary>
    public int LineCount { get; init; }

    /// <summary>Gets the layouts recognised; empty when only known callouts were picked out.</summary>
    public List<string> Formats { get; init; } = new();

    /// <summary>Gets the pages read with text recognition.</summary>
    public List<int> RecognizedPages { get; init; } = new();

    /// <summary>Gets scanned pages that could not be read.</summary>
    public List<int> UnreadablePages { get; init; } = new();

    /// <summary>Gets the auto-match threshold in effect.</summary>
    public int Threshold { get; init; }
}

/// <summary>
/// "Parse PDF into Bulk Add": reads a hardware PDF (text, or scans via <see cref="IPdfOcrEngine"/>),
/// parses its hardware lines (<see cref="HardwarePdfParser"/>), and matches each distinct callout
/// to hardware items (<see cref="HardwareImportMatcher"/>). The apps then show a wizard for the
/// callouts that need review, record the decisions (<see cref="HardwareImportLearningService"/>)
/// and add the result to the job's Bulk Add draft.
/// </summary>
public static class HardwarePdfImportService
{
    /// <summary>Cuts the given pages (1-based, in order; out-of-range ones skipped) out of a PDF, for a preview.</summary>
    /// <param name="pdfPath">The PDF.</param>
    /// <param name="pages">The pages.</param>
    /// <returns>A PDF of those pages, or null when none of them exist.</returns>
    public static byte[]? PagesPdf(string pdfPath, IEnumerable<int> pages)
    {
        using var source = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        using var output = new PdfDocument();
        foreach (var page in pages.Distinct().OrderBy(p => p))
            if (page >= 1 && page <= source.PageCount)
                output.AddPage(source.Pages[page - 1]);
        if (output.PageCount == 0) return null;
        using var ms = new MemoryStream();
        output.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>Analyses a PDF.</summary>
    /// <param name="pdfPath">The PDF.</param>
    /// <param name="ocr">Text recognition for scanned pages, or null.</param>
    /// <param name="progress">Status text.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public static async Task<HardwarePdfAnalysis> AnalyzeAsync(
        string pdfPath, IPdfOcrEngine? ocr, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var text = await HardwarePdfTextReader.ReadAsync(pdfPath, ocr, progress, cancellationToken).ConfigureAwait(false);
        var pages = text.Pages.Select(p => (IReadOnlyList<string>)p).ToList();

        progress?.Report("Finding hardware lines...");
        List<CustomPdfFormat> taught;
        using (var ctx = DatabaseInitializer.CreateContext())
            taught = CustomPdfFormats.Load(ctx);
        // A format the user taught wins over the built-in ones when the PDF looks like it.
        var parsed = CustomPdfFormats.TryParse(taught, pages) ?? HardwarePdfParser.Parse(pages);

        progress?.Report("Matching against the database...");
        HardwareImportKnowledge knowledge;
        using (var ctx = DatabaseInitializer.CreateContext())
            knowledge = HardwareImportKnowledge.Load(ctx);

        var lines = parsed.Lines.Count > 0 ? parsed.Lines : HardwareImportMatcher.FindKnownCallouts(pages, knowledge);
        var groups = HardwareImportMatcher.Match(lines, knowledge);

        progress?.Report("Checking the product catalogs...");
        try
        {
            Catalogs.CatalogImportHints.Apply(groups, new Catalogs.CatalogLookupService(DatabaseInitializer.CreateContext), knowledge);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Catalog hints are extra help only.
        }

        return new HardwarePdfAnalysis
        {
            Groups = groups,
            LineCount = lines.Count,
            Formats = parsed.Formats,
            RecognizedPages = text.RecognizedPages,
            UnreadablePages = text.UnreadablePages,
            Threshold = knowledge.Threshold,
        };
    }
}
