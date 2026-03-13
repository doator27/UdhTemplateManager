using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Stamps sequential page numbers (bottom-right, bold red, size 25) onto a PDF, with a
/// configurable number of leading pages to skip (e.g. to leave a cover sheet unnumbered).
/// </summary>
public class PageNumberer
{
    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <paramref name="outputPath"/>, stamping
    /// sequential page numbers starting at 1 on all pages after the first
    /// <paramref name="skipPages"/> pages.
    /// </summary>
    /// <param name="sourcePath">Full path to the source PDF file.</param>
    /// <param name="skipPages">
    /// Number of leading pages that receive no page number stamp (e.g. cover sheet pages).
    /// </param>
    /// <param name="outputPath">Full path for the output PDF file.</param>
    /// <returns>The <paramref name="outputPath"/> value for method chaining.</returns>
    public string StampPageNumbers(string sourcePath, int skipPages, string outputPath)
    {
        if (skipPages < 0)
            throw new ArgumentOutOfRangeException(nameof(skipPages), "skipPages must be >= 0.");

        using var sourceDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
        using var outputDoc = new PdfDocument();

        var font = ResolveFont();

        int pageNumber = 1;

        for (int i = 0; i < sourceDoc.PageCount; i++)
        {
            var page = outputDoc.AddPage(sourceDoc.Pages[i]);

            if (i < skipPages)
                continue;

            using var gfx = XGraphics.FromPdfPage(page);

            const double rightMargin = 16;
            const double bottomMargin = 16;
            var label = pageNumber.ToString();
            var textSize = gfx.MeasureString(label, font);
            var x = page.Width.Point - rightMargin - textSize.Width;
            var y = page.Height.Point - bottomMargin - textSize.Height;
            gfx.DrawString(label, font, XBrushes.Red, x, y + textSize.Height);

            pageNumber++;
        }

        EnsureDirectory(outputPath);
        outputDoc.Save(outputPath);
        return outputPath;
    }

    /// <summary>
    /// Returns the first available font from a list of cross-platform candidates.
    /// Tries "Arial" (Windows), then "Liberation Sans" and "DejaVu Sans" (Linux).
    /// </summary>
    private static XFont ResolveFont()
    {
        var candidates = new[] { "Arial", "Liberation Sans", "DejaVu Sans", "Helvetica" };
        foreach (var name in candidates)
        {
            try
            {
                return new XFont(name, 25, XFontStyleEx.Bold);
            }
            catch
            {
                // Font not available on this platform — try the next candidate.
            }
        }
        throw new InvalidOperationException(
            "No suitable font found for page numbering. Install Arial, Liberation Sans, or DejaVu Sans.");
    }

    /// <summary>Creates the directory for <paramref name="filePath"/> if it does not exist.</summary>
    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
