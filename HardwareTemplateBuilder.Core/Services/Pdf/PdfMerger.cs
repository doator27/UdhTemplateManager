using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Merges an ordered list of PDF files into a single output PDF, preserving page order.
/// </summary>
public class PdfMerger
{
    /// <summary>
    /// Merges all PDFs listed in <paramref name="pdfPaths"/> (in order) into a single file
    /// at <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="pdfPaths">Ordered list of full paths to the source PDF files.</param>
    /// <param name="outputPath">Full path for the merged output PDF.</param>
    /// <returns>The <paramref name="outputPath"/> value for method chaining.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pdfPaths"/> is empty.</exception>
    public string Merge(IReadOnlyList<string> pdfPaths, string outputPath)
    {
        if (pdfPaths.Count == 0)
            throw new ArgumentException("At least one PDF path must be provided.", nameof(pdfPaths));

        using var outputDoc = new PdfDocument();

        foreach (var path in pdfPaths)
        {
            using var sourceDoc = PdfReader.Open(path, PdfDocumentOpenMode.Import);

            foreach (var page in sourceDoc.Pages)
            {
                outputDoc.AddPage(page);
            }
        }

        EnsureDirectory(outputPath);
        outputDoc.Save(outputPath);
        return outputPath;
    }

    /// <summary>Creates the directory for <paramref name="filePath"/> if it does not exist.</summary>
    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
