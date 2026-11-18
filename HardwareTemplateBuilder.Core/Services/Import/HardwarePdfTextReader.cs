using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>The text of a PDF, one list of layout lines per page.</summary>
public sealed class PdfDocumentText
{
    /// <summary>Gets the layout lines of each page (index = page index).</summary>
    public List<List<string>> Pages { get; } = new();

    /// <summary>Gets the 1-based numbers of pages that were read with text recognition.</summary>
    public List<int> RecognizedPages { get; } = new();

    /// <summary>Gets the 1-based numbers of image-only pages that could not be read (no OCR available).</summary>
    public List<int> UnreadablePages { get; } = new();
}

/// <summary>
/// Reads a PDF's text with PdfPig, keeping the column layout (<see cref="LayoutTextBuilder"/>).
/// Pages with (almost) no text are treated as scans and read with the supplied
/// <see cref="IPdfOcrEngine"/> when one is available.
/// </summary>
public static class HardwarePdfTextReader
{
    /// <summary>A page with fewer words than this is treated as a scan.</summary>
    public const int ScannedPageWordThreshold = 5;

    /// <summary>Reads every page of <paramref name="pdfPath"/>.</summary>
    /// <param name="pdfPath">The PDF file.</param>
    /// <param name="ocr">Text recognition for scanned pages, or null.</param>
    /// <param name="progress">Receives status text such as "Reading page 3 of 12...".</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public static async Task<PdfDocumentText> ReadAsync(
        string pdfPath, IPdfOcrEngine? ocr, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var result = new PdfDocumentText();
        var scanned = new List<int>();

        using (var document = PdfDocument.Open(pdfPath))
        {
            int count = document.NumberOfPages;
            for (int n = 1; n <= count; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Reading page {n} of {count}...");
                var page = document.GetPage(n);
                // Words are built from the glyphs (PdfLetterWords): some PDFs have no space
                // characters, and only the gaps between glyphs show where words end. Rotated
                // text (dimensions on drawings) is left out.
                var words = PdfLetterWords.Group(page.Letters
                    .Where(l => l.TextOrientation == TextOrientation.Horizontal)
                    .Select(l => new PdfLetter(
                        l.StartBaseLine.X,
                        Math.Max(l.EndBaseLine.X, l.StartBaseLine.X + l.Width),
                        page.Height - l.StartBaseLine.Y,
                        l.PointSize > 0 ? l.PointSize : Math.Max(1, l.GlyphRectangle.Height),
                        l.Value)));
                if (words.Count < ScannedPageWordThreshold) scanned.Add(n - 1);
                result.Pages.Add(LayoutTextBuilder.BuildLines(words));
            }
        }

        foreach (var index in scanned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ocr == null || !ocr.IsAvailable)
            {
                result.UnreadablePages.Add(index + 1);
                continue;
            }
            progress?.Report($"Recognising text on scanned page {index + 1} of {result.Pages.Count}...");
            var words = await ocr.RecognizePageAsync(pdfPath, index, cancellationToken).ConfigureAwait(false);
            result.Pages[index] = LayoutTextBuilder.BuildLines(words);
            result.RecognizedPages.Add(index + 1);
        }
        return result;
    }
}
