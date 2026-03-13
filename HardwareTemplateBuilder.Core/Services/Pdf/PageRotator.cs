using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Applies a uniform rotation to a specified set of pages within a PDF and writes the
/// result to an output file.
/// </summary>
public class PageRotator
{
    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <paramref name="outputPath"/>, rotating the
    /// pages listed in <paramref name="pageNumbers"/> by <paramref name="degrees"/>.
    /// </summary>
    /// <param name="sourcePath">Full path to the source PDF file.</param>
    /// <param name="pageNumbers">1-based page numbers that should be rotated.</param>
    /// <param name="degrees">
    /// Rotation in degrees to add to each target page's existing rotation.
    /// Must be a multiple of 90. Positive values rotate clockwise; negative counterclockwise.
    /// </param>
    /// <param name="outputPath">Full path for the output PDF file.</param>
    /// <returns>The <paramref name="outputPath"/> value for method chaining.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="degrees"/> is not a multiple of 90.
    /// </exception>
    public string Rotate(string sourcePath, IReadOnlyList<int> pageNumbers, int degrees, string outputPath)
    {
        var normalizedDegrees = NormalizeDegrees(degrees);

        using var sourceDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
        using var outputDoc = new PdfDocument();

        var rotateSet = new HashSet<int>(pageNumbers);

        for (int i = 0; i < sourceDoc.PageCount; i++)
        {
            var page = outputDoc.AddPage(sourceDoc.Pages[i]);

            // Page numbers are 1-based; i is 0-based.
            if (rotateSet.Contains(i + 1))
            {
                page.Rotate = (page.Rotate + normalizedDegrees) % 360;
            }
        }

        EnsureDirectory(outputPath);
        outputDoc.Save(outputPath);
        return outputPath;
    }

    /// <summary>
    /// Normalizes <paramref name="degrees"/> to a value in [0, 360) that is a multiple of 90.
    /// Positive input is treated as clockwise; negative as counterclockwise.
    /// </summary>
    private static int NormalizeDegrees(int degrees)
    {
        if (degrees % 90 != 0)
            throw new ArgumentException(
                $"Rotation must be a multiple of 90 degrees. Got: {degrees}", nameof(degrees));

        return ((degrees % 360) + 360) % 360;
    }

    /// <summary>Creates the directory for <paramref name="filePath"/> if it does not exist.</summary>
    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
