using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;

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
        var parsed = HardwarePdfParser.Parse(pages);

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
