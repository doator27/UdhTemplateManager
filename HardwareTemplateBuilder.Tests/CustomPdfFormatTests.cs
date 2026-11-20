using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services.Import;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for PDF formats taught by the user (<see cref="CustomPdfFormats"/>).</summary>
public class CustomPdfFormatTests
{
    private static List<IReadOnlyList<string>> Pages(params string[][] pages) => pages.Select(p => (IReadOnlyList<string>)p.ToList()).ToList();

    private static IReadOnlyList<IReadOnlyList<string>> LoadPages(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "PdfImport", name);
        var pages = new List<IReadOnlyList<string>>();
        List<string>? current = null;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith("=== PAGE")) { current = new List<string>(); pages.Add(current); continue; }
            current?.Add(line);
        }
        return pages;
    }

    [Fact]
    public void HeaderFormat_GuessedFromTheHeaderAndReadsEveryPage()
    {
        var pages = LoadPages("hardware_list.txt");
        var header = pages[0].First(l => l.Contains("Product Number"));
        var example = pages[0].First(l => l.Contains("Continuous Hinge"));
        var cols = CustomPdfFormats.GuessColumns(example, header);
        Assert.Equal(new[] { PdfFormatField.Manufacturer, PdfFormatField.Description, PdfFormatField.Catalog, PdfFormatField.Finish },
            cols.Select(c => c.Field));

        var format = new CustomPdfFormat { Name = "List", RecognizeBy = { "Hardware List" }, HeaderLine = header, Columns = cols, QuantityRequired = false };
        Assert.True(CustomPdfFormats.Matches(format, pages));
        var r = CustomPdfFormats.Parse(format, pages);

        Assert.Equal(59, r.Lines.Count);
        Assert.Equal("Electromagnetic Door Holder", r.Lines.Single(l => l.Catalog == "2100").GenericName);
        var silencer = r.Lines.Single(l => l.GenericName == "Silencer");
        Assert.Equal("TR", silencer.ManufacturerCode);
        Assert.Equal("GREY", silencer.Finish);
        Assert.Contains("Taught format: List", r.Formats);
    }

    [Fact]
    public void PositionFormat_WithQuantityHeadingsAndSkippedLines()
    {
        var page = new[]
        {
            "ACME DOOR HARDWARE TAKEOFF",
            "Group: A1 Entry",
            "   3   Ives       5BB1 4.5 x 4.5 NRP        652   Butt hinge",
            "   1   Schlage    ND80PD RHO RH             626   Storeroom lock",
            "       Note: confirm keying",
            "   1   LCN        4040XP RW/PA              689   Closer",
            "Group: A2 Office",
            "   2   Ives       5BB1 4.5 x 4.5            652   Butt hinge",
            "  3    Ives       5BB1 4.5 x 4.5            652   Butt hinge",
            "Page 1 of 1",
        };
        var pages = Pages(page);
        var cols = CustomPdfFormats.GuessColumns(page[2], null);
        Assert.Equal(new[] { PdfFormatField.Quantity, PdfFormatField.Manufacturer, PdfFormatField.Catalog, PdfFormatField.Finish, PdfFormatField.Description },
            cols.Select(c => c.Field));

        var format = new CustomPdfFormat
        {
            Name = "Acme",
            RecognizeBy = { "acme door hardware takeoff" },
            Columns =
            {
                new PdfFormatColumn { Start = 3, Field = PdfFormatField.Quantity },
                new PdfFormatColumn { Start = 7, Field = PdfFormatField.Manufacturer },
                new PdfFormatColumn { Start = 18, Field = PdfFormatField.Catalog },
                new PdfFormatColumn { Start = 44, Field = PdfFormatField.Finish },
                new PdfFormatColumn { Start = 50, Field = PdfFormatField.Description },
            },
            HeadingPrefix = "Group:",
            SkipLinesStartingWith = { "Note:" },
        };
        Assert.True(CustomPdfFormats.Matches(format, pages));
        var r = CustomPdfFormats.Parse(format, pages);

        Assert.Equal(5, r.Lines.Count);
        var hinge = r.Lines[0];
        Assert.Equal(3, hinge.Quantity);
        Assert.Equal("IVES", hinge.ManufacturerCode);
        Assert.Equal("Ives", hinge.ManufacturerName);
        Assert.Equal("5BB1 4.5 x 4.5 NRP", hinge.Catalog);
        Assert.Equal("652", hinge.Finish);
        Assert.Equal("Butt hinge", hinge.GenericName);
        Assert.Equal("A1 Entry", hinge.SetName);
        var lockLine = r.Lines[1];
        Assert.Equal("ND80PD RHO", lockLine.Catalog);
        Assert.Equal("RH", lockLine.Handing);
        Assert.Equal("A2 Office", r.Lines[3].SetName);
        // Slightly shifted columns (a quantity one character over) still read.
        Assert.Equal(3, r.Lines[4].Quantity);

        // A PDF without the recognize text isn't this format.
        Assert.False(CustomPdfFormats.Matches(format, Pages(new[] { "Something else", "   3   Ives   5BB1" })));
        Assert.Null(CustomPdfFormats.TryParse(new[] { format }, Pages(new[] { "Something else" })));
    }

    [Fact]
    public void ManufacturerNameStartsTheCatalog()
    {
        var pages = Pages(new[]
        {
            "Frame Prep List",
            "  2     McKinney TA2714 4 1/2\" x 4 1/2\" US26D",
            "  1     Norton Rixson 7500-REG 689",
        });
        var format = new CustomPdfFormat
        {
            Name = "Prep",
            RecognizeBy = { "Frame Prep List" },
            Columns = { new PdfFormatColumn { Start = 2, Field = PdfFormatField.Quantity }, new PdfFormatColumn { Start = 8, Field = PdfFormatField.Catalog } },
            ManufacturerInCatalog = true,
        };
        var r = CustomPdfFormats.Parse(format, pages);
        Assert.Equal(new[] { "McKinney", "Norton Rixson" }, r.Lines.Select(l => l.ManufacturerName));
        Assert.Equal("7500-REG", r.Lines[1].Catalog);
        Assert.Equal("689", r.Lines[1].Finish);
    }

    [Fact]
    public void FormatsAreStoredAndReplacedByName()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"HtbFormats_{Guid.NewGuid():N}").Options;
        using var ctx = new AppDbContext(options);
        CustomPdfFormats.Upsert(ctx, new CustomPdfFormat { Name = "Acme", RecognizeBy = { "ACME" }, Columns = { new PdfFormatColumn { Start = 1, Field = PdfFormatField.Catalog } } });
        CustomPdfFormats.Upsert(ctx, new CustomPdfFormat { Name = "acme", RecognizeBy = { "ACME 2" } });
        CustomPdfFormats.Upsert(ctx, new CustomPdfFormat { Name = "Beta", RecognizeBy = { "B" } });
        var all = CustomPdfFormats.Load(ctx);
        Assert.Equal(new[] { "acme", "Beta" }, all.Select(f => f.Name));
        Assert.Equal("ACME 2", all[0].RecognizeBy.Single());
        CustomPdfFormats.Remove(ctx, "BETA");
        Assert.Single(CustomPdfFormats.Load(ctx));
        Assert.Contains("Used for PDFs containing", CustomPdfFormats.Describe(all[0]));
    }

    [Fact]
    public void LabelsNameTheirFields()
    {
        Assert.Equal(PdfFormatField.Quantity, CustomPdfFormats.FieldFromLabel("Qty"));
        Assert.Equal(PdfFormatField.Manufacturer, CustomPdfFormats.FieldFromLabel("Mfr."));
        Assert.Equal(PdfFormatField.Description, CustomPdfFormats.FieldFromLabel("Product Description"));
        Assert.Equal(PdfFormatField.Catalog, CustomPdfFormats.FieldFromLabel("Catalog Number"));
        Assert.Equal(PdfFormatField.Finish, CustomPdfFormats.FieldFromLabel("Finish"));
        Assert.Equal(PdfFormatField.Ignore, CustomPdfFormats.FieldFromLabel("Remarks"));
        Assert.Equal("Hardware List", CustomPdfFormats.SuggestRecognizeText(LoadPages("hardware_list.txt")));
    }
}
