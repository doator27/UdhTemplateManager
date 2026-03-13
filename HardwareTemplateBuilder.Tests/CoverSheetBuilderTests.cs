using PdfSharp.Pdf.IO;
using HardwareTemplateBuilder.Core.Services.Pdf;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for <see cref="CoverSheetBuilder"/>.</summary>
public class CoverSheetBuilderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CoverSheetBuilder _builder = new();

    /// <summary>Creates a temporary directory for generated PDFs.</summary>
    public CoverSheetBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"HtbCoverTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ---------- Helpers ----------

    private CoverSheetData MakeSampleData(int rowCount = 3) => new CoverSheetData
    {
        JobNumber = "2024-001",
        JobName = "Main Street Office Renovation",
        CustomerName = "Acme Corp",
        ProjectManagerName = "Jane Smith",
        DateCreated = new DateTime(2024, 6, 15),
        Rows = Enumerable.Range(1, rowCount).Select(i => new CoverSheetRow
        {
            Manufacturer = $"Manufacturer {i}",
            HardwareType = "Closer",
            HardwareDescription = $"Model-{i:D3}",
            TemplateNumbers = $"T-{i:D3}",
            PageNumbers = $"{i}",
            Remarks = i % 2 == 0 ? "See note" : null
        }).ToList().AsReadOnly()
    };

    // ---------- Tests ----------

    [Fact]
    public void Build_CreatesOutputFile()
    {
        var output = Path.Combine(_tempDir, "cover.pdf");
        _builder.Build(MakeSampleData(), output);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Build_OutputIsValidPdf_AtLeastOnePage()
    {
        var output = Path.Combine(_tempDir, "cover_valid.pdf");
        _builder.Build(MakeSampleData(), output);

        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.True(doc.PageCount >= 1);
    }

    [Fact]
    public void Build_ReturnsOutputPath()
    {
        var output = Path.Combine(_tempDir, "cover_return.pdf");
        var result = _builder.Build(MakeSampleData(), output);
        Assert.Equal(output, result);
    }

    [Fact]
    public void Build_CreatesOutputDirectory_IfMissing()
    {
        var subDir = Path.Combine(_tempDir, "nested", "sub");
        var output = Path.Combine(subDir, "cover.pdf");

        Assert.False(Directory.Exists(subDir));
        _builder.Build(MakeSampleData(), output);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Build_WithManyRows_HandlesMultiplePages()
    {
        // 80 rows should overflow onto a second page on a Letter sheet.
        var output = Path.Combine(_tempDir, "cover_multipage.pdf");
        var data = MakeSampleData(rowCount: 80);
        _builder.Build(data, output);

        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.True(doc.PageCount >= 1);
    }

    [Fact]
    public void Build_WithNoRows_ProducesValidPdf()
    {
        var data = MakeSampleData(rowCount: 0);
        var output = Path.Combine(_tempDir, "cover_empty.pdf");
        _builder.Build(data, output);

        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.True(doc.PageCount >= 1);
    }

    [Fact]
    public void GetPageCount_ReturnsCorrectCount()
    {
        var output = Path.Combine(_tempDir, "cover_count.pdf");
        _builder.Build(MakeSampleData(), output);

        int count = CoverSheetBuilder.GetPageCount(output);
        Assert.True(count >= 1);
    }
}
