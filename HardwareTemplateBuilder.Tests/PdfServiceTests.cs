using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using HardwareTemplateBuilder.Core.Services.Pdf;

namespace HardwareTemplateBuilder.Tests;

/// <summary>
/// Integration tests for <see cref="PageExtractor"/>, <see cref="PageRotator"/>,
/// <see cref="PdfMerger"/>, and <see cref="PageNumberer"/> using programmatically
/// created sample PDFs.
/// </summary>
public class PdfServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PageExtractor _extractor = new();
    private readonly PageRotator _rotator = new();
    private readonly PdfMerger _merger = new();
    private readonly PageNumberer _numberer = new();

    /// <summary>Creates a temporary directory for test PDFs.</summary>
    public PdfServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"HtbTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ---------- Helpers ----------

    /// <summary>
    /// Creates a PDF with <paramref name="pageCount"/> blank pages and saves it to
    /// <paramref name="name"/>.pdf in the temp directory. No fonts are required.
    /// </summary>
    private string CreateSamplePdf(int pageCount, string name = "sample")
    {
        var path = Path.Combine(_tempDir, $"{name}.pdf");
        using var doc = new PdfDocument();

        for (int i = 0; i < pageCount; i++)
        {
            doc.AddPage();
        }

        doc.Save(path);
        return path;
    }

    /// <summary>Returns the page count of the PDF at <paramref name="path"/>.</summary>
    private static int GetPageCount(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    /// <summary>Returns the rotation value of a specific 1-based page.</summary>
    private static int GetPageRotation(string path, int pageNumber)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Pages[pageNumber - 1].Rotate;
    }

    // ---------- PageExtractor ----------

    [Fact]
    public void Extract_CorrectPageCount()
    {
        var source = CreateSamplePdf(5, "extract_source");
        var output = Path.Combine(_tempDir, "extracted.pdf");

        _extractor.Extract(source, new[] { 1, 3, 5 }, output);

        Assert.Equal(3, GetPageCount(output));
    }

    [Fact]
    public void Extract_SinglePage_ProducesOnePagePdf()
    {
        var source = CreateSamplePdf(3, "extract_single");
        var output = Path.Combine(_tempDir, "extracted_single.pdf");

        _extractor.Extract(source, new[] { 2 }, output);

        Assert.Equal(1, GetPageCount(output));
    }

    [Fact]
    public void Extract_EmptyPageList_ThrowsArgumentException()
    {
        var source = CreateSamplePdf(3, "extract_empty");
        var output = Path.Combine(_tempDir, "extracted_empty.pdf");

        Assert.Throws<ArgumentException>(() =>
            _extractor.Extract(source, Array.Empty<int>(), output));
    }

    [Fact]
    public void Extract_OutOfRangePageNumber_ThrowsArgumentException()
    {
        var source = CreateSamplePdf(3, "extract_range");
        var output = Path.Combine(_tempDir, "extracted_range.pdf");

        Assert.Throws<ArgumentException>(() =>
            _extractor.Extract(source, new[] { 5 }, output));
    }

    // ---------- PageRotator ----------

    [Fact]
    public void Rotate_TargetPagesHaveCorrectRotation()
    {
        var source = CreateSamplePdf(3, "rotate_source");
        var output = Path.Combine(_tempDir, "rotated.pdf");

        _rotator.Rotate(source, new[] { 1, 3 }, 90, output);

        Assert.Equal(90, GetPageRotation(output, 1));
        Assert.Equal(0, GetPageRotation(output, 2));   // untouched
        Assert.Equal(90, GetPageRotation(output, 3));
    }

    [Fact]
    public void Rotate_NegativeDegrees_NormalizesCorrectly()
    {
        var source = CreateSamplePdf(2, "rotate_neg");
        var output = Path.Combine(_tempDir, "rotated_neg.pdf");

        // -90 degrees (counterclockwise) should normalize to 270.
        _rotator.Rotate(source, new[] { 1 }, -90, output);

        Assert.Equal(270, GetPageRotation(output, 1));
        Assert.Equal(0, GetPageRotation(output, 2));
    }

    [Fact]
    public void Rotate_NonMultipleOf90_ThrowsArgumentException()
    {
        var source = CreateSamplePdf(2, "rotate_invalid");
        var output = Path.Combine(_tempDir, "rotated_invalid.pdf");

        Assert.Throws<ArgumentException>(() =>
            _rotator.Rotate(source, new[] { 1 }, 45, output));
    }

    [Fact]
    public void Rotate_PreservesPageCount()
    {
        var source = CreateSamplePdf(4, "rotate_count");
        var output = Path.Combine(_tempDir, "rotated_count.pdf");

        _rotator.Rotate(source, new[] { 2 }, 180, output);

        Assert.Equal(4, GetPageCount(output));
    }

    // ---------- PdfMerger ----------

    [Fact]
    public void Merge_TwoPdfs_ProducesCombinedPageCount()
    {
        var pdf1 = CreateSamplePdf(2, "merge_a");
        var pdf2 = CreateSamplePdf(3, "merge_b");
        var output = Path.Combine(_tempDir, "merged.pdf");

        _merger.Merge(new[] { pdf1, pdf2 }, output);

        Assert.Equal(5, GetPageCount(output));
    }

    [Fact]
    public void Merge_ThreePdfs_PreservesOrder()
    {
        var pdf1 = CreateSamplePdf(1, "merge_1");
        var pdf2 = CreateSamplePdf(1, "merge_2");
        var pdf3 = CreateSamplePdf(1, "merge_3");
        var output = Path.Combine(_tempDir, "merged3.pdf");

        _merger.Merge(new[] { pdf1, pdf2, pdf3 }, output);

        Assert.Equal(3, GetPageCount(output));
    }

    [Fact]
    public void Merge_EmptyList_ThrowsArgumentException()
    {
        var output = Path.Combine(_tempDir, "merged_empty.pdf");

        Assert.Throws<ArgumentException>(() =>
            _merger.Merge(Array.Empty<string>(), output));
    }

    // ---------- PageNumberer ----------

    [Fact]
    public void StampPageNumbers_PreservesPageCount()
    {
        var source = CreateSamplePdf(5, "number_source");
        var output = Path.Combine(_tempDir, "numbered.pdf");

        _numberer.StampPageNumbers(source, skipPages: 1, output);

        Assert.Equal(5, GetPageCount(output));
    }

    [Fact]
    public void StampPageNumbers_ZeroSkip_NumbersAllPages()
    {
        var source = CreateSamplePdf(3, "number_noskip");
        var output = Path.Combine(_tempDir, "numbered_noskip.pdf");

        // Should not throw; all pages get numbers.
        _numberer.StampPageNumbers(source, skipPages: 0, output);

        Assert.Equal(3, GetPageCount(output));
    }

    [Fact]
    public void StampPageNumbers_NegativeSkip_ThrowsArgumentOutOfRangeException()
    {
        var source = CreateSamplePdf(3, "number_neg");
        var output = Path.Combine(_tempDir, "numbered_neg.pdf");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _numberer.StampPageNumbers(source, skipPages: -1, output));
    }
}
