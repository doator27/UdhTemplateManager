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

            StampNumber(page, font, pageNumber);
            pageNumber++;
        }

        EnsureDirectory(outputPath);
        outputDoc.Save(outputPath);
        return outputPath;
    }

    /// <summary>
    /// Stamps a single page number in the visual bottom-right corner, accounting for the
    /// page's <c>/Rotate</c> entry so the label lands in the correct visual position regardless
    /// of how the viewer has rotated the page.
    /// </summary>
    /// <remarks>
    /// PdfSharp draws in the page's native (pre-rotation) coordinate space.  For a page with
    /// <c>/Rotate: 90</c> the viewer rotates the content 90° CW, so the native x-axis maps to
    /// the display y-axis.  The stamp coordinates are computed per-rotation so the label always
    /// appears near the visual bottom-right corner.  The label may appear sideways on 90°/270°
    /// rotated pages — it is still legible for 1–2 digit numbers.
    /// </remarks>
    private static void StampNumber(PdfPage page, XFont font, int pageNumber)
    {
        const double rightMargin  = 16;
        const double bottomMargin = 16;

        var label = pageNumber.ToString();
        using var gfx = XGraphics.FromPdfPage(page);
        var s = gfx.MeasureString(label, font);

        var W = page.Width.Point;
        var H = page.Height.Point;

        // For each rotation, compute the native XGraphics (top-left origin, Y-down) coordinates
        // that correspond to the visual bottom-right corner of the displayed page.
        //   Rotate  0  → display W×H  → native = display coords
        //   Rotate 90  → display H×W  → native(nx,ny): nx = display_y, ny = H - display_x
        //   Rotate 180 → display W×H  → native(nx,ny): nx = W - display_x, ny = H - display_y
        //   Rotate 270 → display H×W  → native(nx,ny): nx = W - display_y, ny = display_x
        double x, y;
        switch (page.Rotate)
        {
            case 90:
                // Display is H wide × W tall.  Visual bottom-right baseline in display:
                //   display_x = H - rightMargin - s.Width,  display_y = W - bottomMargin
                // → native: nx = W - bottomMargin,  ny = H - (H - rightMargin - s.Width) = rightMargin + s.Width
                x = W - bottomMargin - s.Height; // shift left so text body doesn't clip page edge
                y = rightMargin + s.Width;
                break;

            case 180:
                // Display is W×H rotated 180°.  Visual bottom-right in display:
                //   display_x = W - rightMargin - s.Width,  display_y (baseline) = H - bottomMargin
                // → native: nx = W - display_x = rightMargin + s.Width,  ny = H - display_y = bottomMargin
                // DrawString baseline = ny, but for 180° the text is upside-down — still legible for numbers.
                x = rightMargin;
                y = bottomMargin + s.Height;
                break;

            case 270:
                // Display is H wide × W tall, rotated 270° CW (= 90° CCW).
                // → native: nx = W - display_y = W - (W - bottomMargin) = bottomMargin,
                //           ny = display_x = H - rightMargin - s.Width
                x = bottomMargin;
                y = H - rightMargin - s.Width + s.Height;
                break;

            default: // 0 — standard orientation
                x = W - rightMargin - s.Width;
                y = H - bottomMargin;
                break;
        }

        gfx.DrawString(label, font, XBrushes.Red, x, y);
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
