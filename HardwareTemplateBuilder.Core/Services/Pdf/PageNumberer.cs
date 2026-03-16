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
    /// Stamps a single page number in the visual bottom-right corner, always upright,
    /// regardless of the page's <c>/Rotate</c> value.
    /// </summary>
    /// <remarks>
    /// PdfSharp draws in the page's native (pre-rotation) coordinate space (origin top-left,
    /// Y-down). The PDF viewer then applies <c>/Rotate</c> (clockwise) when rendering.
    /// <para>
    /// This method applies an <see cref="XMatrix"/> that is the inverse of the viewer's
    /// rotation, mapping visual (display) coordinates back to native coordinates.
    /// Drawing at the visual bottom-right corner in that transformed space produces text
    /// that is always upright and in the correct physical corner after the viewer rotates.
    /// </para>
    /// Inverse rotation matrices (native W×H; display dims after CW R°):
    /// <code>
    ///   R=0:   identity                      visW=W, visH=H
    ///   R=90:  [ 0, 1,-1, 0, W, 0]           visW=H, visH=W
    ///   R=180: [-1, 0, 0,-1, W, H]           visW=W, visH=H
    ///   R=270: [ 0,-1, 1, 0, 0, H]           visW=H, visH=W
    /// </code>
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

        // Visual (display) dimensions after the viewer applies /Rotate.
        double visW = (page.Rotate == 90 || page.Rotate == 270) ? H : W;
        double visH = (page.Rotate == 90 || page.Rotate == 270) ? W : H;

        // Matrix that maps visual coordinates → native coordinates,
        // undoing the viewer's CW /Rotate so the stamped text is always upright.
        // Derivation: viewer applies native(x,y) → display via R=90: (H-y, x); R=270: (y, W-x).
        // Inverse (display→native): R=90: (vy, H-vx); R=270: (W-vy, vx).
        XMatrix matrix = page.Rotate switch
        {
            90  => new XMatrix( 0, -1,  1,  0, 0, H),
            180 => new XMatrix(-1,  0,  0, -1, W, H),
            270 => new XMatrix( 0,  1, -1,  0, W, 0),
            _   => new XMatrix( 1,  0,  0,  1, 0, 0) // identity
        };

        // DrawString: x = left edge of text, y = baseline (≈ bottom of glyph box).
        double drawX = visW - rightMargin - s.Width;
        double drawY = visH - bottomMargin;

        var state = gfx.Save();
        gfx.MultiplyTransform(matrix);
        gfx.DrawString(label, font, XBrushes.Red, drawX, drawY);
        gfx.Restore(state);
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
