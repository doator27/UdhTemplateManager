using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Extracts a specified subset of pages from a source PDF and writes them to a new file.
/// </summary>
public class PageExtractor
{
    /// <summary>
    /// Extracts the pages identified by <paramref name="pageNumbers"/> from
    /// <paramref name="sourcePath"/> and saves the result to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="sourcePath">Full path to the source PDF file.</param>
    /// <param name="pageNumbers">
    /// 1-based page numbers to extract, in the order they should appear in the output.
    /// </param>
    /// <param name="outputPath">Full path for the output PDF file.</param>
    /// <returns>The <paramref name="outputPath"/> value for method chaining.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="pageNumbers"/> is empty or a page number is out of range.
    /// </exception>
    public string Extract(string sourcePath, IReadOnlyList<int> pageNumbers, string outputPath)
    {
        if (pageNumbers.Count == 0)
            throw new ArgumentException("At least one page number must be specified.", nameof(pageNumbers));

        using var sourceDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);

        foreach (var pageNum in pageNumbers)
        {
            if (pageNum < 1 || pageNum > sourceDoc.PageCount)
                throw new ArgumentException(
                    $"Page number {pageNum} is out of range for a {sourceDoc.PageCount}-page document.",
                    nameof(pageNumbers));
        }

        using var outputDoc = new PdfDocument();

        foreach (var pageNum in pageNumbers)
        {
            outputDoc.AddPage(sourceDoc.Pages[pageNum - 1]);
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
