using HardwareTemplateBuilder.Core.Services.Import;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for the hardware PDF parsers (text excerpts from real schedules/costing reports).</summary>
public class PdfImportParserTests
{
    private static IReadOnlyList<IReadOnlyList<string>> Load(string name) =>
        new[] { (IReadOnlyList<string>)File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", "PdfImport", name)) };

    private static ParsedHardwareLine Find(HardwarePdfParseResult r, string catalogStart) =>
        r.Lines.First(l => l.Catalog.StartsWith(catalogStart, StringComparison.Ordinal));

    [Fact]
    public void Schedule_ReadsItemLinesWithColumns()
    {
        var r = HardwarePdfParser.Parse(Load("schedule.txt"));

        Assert.Contains("Hardware schedule", r.Formats);
        var hinge = Find(r, "5BB1 4 1/2 x 4 1/2 NRP");
        Assert.Equal("IV", hinge.ManufacturerCode);
        Assert.Equal("Ives", hinge.ManufacturerName);
        Assert.Equal("Hinges", hinge.GenericName);
        Assert.Equal("651", hinge.Finish);
        Assert.Equal(270, hinge.Quantity);
        Assert.Equal("080-TYP", hinge.SetName);

        var closer = Find(r, "4040XP REGARM");
        Assert.Equal("LC", closer.ManufacturerCode);
        Assert.Equal("4040XP REGARM", closer.Catalog);
        Assert.Equal("AL", closer.Finish);
    }

    [Fact]
    public void Schedule_JoinsWrappedLinesAndDropsHanding()
    {
        var r = HardwarePdfParser.Parse(Load("schedule.txt"));

        var lock1 = r.Lines.Where(l => l.Catalog.StartsWith("5RS-853-EL", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lock1.Count);
        Assert.All(lock1, l =>
        {
            Assert.Equal("5RS-853-EL(FAIL SAFE)-G6-W10(REX) \"ROBODUE\" 2 3/4\"BS, 1 3/4\"DR", l.Catalog);
            Assert.Equal("Electrified Mortise Lockset", l.GenericName);
            Assert.Equal("MK01", l.ManufacturerCode);
        });
        Assert.Equal(new[] { "RHR", "LHR" }, lock1.Select(l => l.Handing));

        var trim = Find(r, "KRS 853 FD-SA-G6-T2");
        Assert.StartsWith("KRS 853 FD-SA-G6-T2 \"ROBODUE\" INCLUDE O/S& I/S LEVER/ROSE", trim.Catalog);
        Assert.Equal(610, trim.Quantity);
    }

    [Fact]
    public void Schedule_SplitsLongNamesAndKeepsCommaQuantities()
    {
        var r = HardwarePdfParser.Parse(Load("schedule.txt"));

        var contact = Find(r, "8080-T-W");
        Assert.Equal("Door Contact (By Others)", contact.GenericName);
        Assert.Equal("VA01", contact.ManufacturerCode);

        var hinges = r.Lines.First(l => l.Catalog == "5BB1 4 1/2 x 4 1/2");
        Assert.Equal(3897, hinges.Quantity);
        Assert.Equal("090", hinges.SetName);

        var viewer = Find(r, "624");
        Assert.Equal("624", viewer.Catalog);
        Assert.Equal("CRM", viewer.Finish);
    }

    [Fact]
    public void Schedule_ToleratesOcrMistakes()
    {
        var r = HardwarePdfParser.Parse(Load("scan_ocr.txt"));

        Assert.NotEmpty(r.Lines);
        Assert.Contains(r.Lines, l => l.ManufacturerCode == "S501");   // read as "$501"
        Assert.Contains(r.Lines, l => l.ManufacturerCode == "AB01");   // read as "ABO1"
        Assert.Contains(r.Lines, l => l.Catalog.StartsWith("8080-T-W", StringComparison.Ordinal));
    }

    [Fact]
    public void Costing_ReadsWrappedDescriptions()
    {
        var r = HardwarePdfParser.Parse(Load("costing.txt"));

        Assert.Contains("Component costing", r.Formats);
        var closer = r.Lines.First(l => l.Catalog.StartsWith("2033", StringComparison.Ordinal));
        Assert.Equal("LC", closer.ManufacturerCode);
        Assert.Equal("Concealed Closer", closer.GenericName);

        var bottom = r.Lines.First(l => l.Catalog.StartsWith("369AA", StringComparison.Ordinal));
        Assert.Equal("ZE", bottom.ManufacturerCode);
        Assert.Equal("Auto Door Bottom", bottom.GenericName);
        Assert.Equal("CUSTOM(2), CUSTOM.1", bottom.SetName);

        var hinge = r.Lines.First(l => l.Catalog.StartsWith("TE 640 3D A8", StringComparison.Ordinal));
        Assert.Equal("S701", hinge.ManufacturerCode);
        Assert.Equal("Concealed Hinge", hinge.GenericName);
    }

    [Fact]
    public void LayoutBuilder_KeepsColumns()
    {
        var words = new List<PdfWord>
        {
            new(10, 100, 34, 110, "H026"), new(60, 100, 78, 110, "270"), new(90, 100, 126, 110, "Hinges"),
            new(200, 100, 224, 110, "5BB1"), new(230, 100, 236, 110, "4"), new(400, 100, 418, 110, "651"), new(500, 100, 512, 110, "IV"),
            new(200, 112, 248, 122, "\"ROBODUE\""),
        };
        var lines = LayoutTextBuilder.BuildLines(words);

        Assert.Equal(2, lines.Count);
        Assert.Matches(@"^\s*H026\s{2,}270\s{2,}Hinges\s{2,}5BB1 4\s{2,}651\s{2,}IV$", lines[0]);
        Assert.Equal(lines[0].IndexOf("5BB1", StringComparison.Ordinal), lines[1].IndexOf("\"ROBODUE\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TextReader_ReadsARealPdf()
    {
        if (!OperatingSystem.IsWindows()) return; // PDFsharp needs Windows fonts to draw the test PDF.

        var path = Path.Combine(Path.GetTempPath(), $"htb_import_{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                using var gfx = XGraphics.FromPdfPage(page);
                var font = new XFont("Arial", 9);
                void At(double x, double y, string t) => gfx.DrawString(t, font, XBrushes.Black, x, y);
                At(40, 60, "Heading #212 (Set #080-TYP)");
                At(40, 90, "C027"); At(80, 90, "90"); At(110, 90, "Closer"); At(220, 90, "4040XP REGARM"); At(420, 90, "AL"); At(500, 90, "LC");
                At(40, 104, "L047"); At(80, 104, "60"); At(110, 104, "Electrified Mortise"); At(220, 104, "5RS-853-EL-G6"); At(360, 104, "RHR"); At(420, 104, "26"); At(500, 104, "MK01");
                At(110, 116, "Lockset"); At(220, 116, "\"ROBODUE\" 2 3/4\"BS");
                doc.Save(path);
            }

            var text = await HardwarePdfTextReader.ReadAsync(path, null, null, CancellationToken.None);
            var r = HardwarePdfParser.Parse(text.Pages.Select(p => (IReadOnlyList<string>)p).ToList());

            var closer = r.Lines.Single(l => l.ManufacturerCode == "LC");
            Assert.Equal("4040XP REGARM", closer.Catalog);
            Assert.Equal("080-TYP", closer.SetName);
            var lockset = r.Lines.Single(l => l.ManufacturerCode == "MK01");
            Assert.Equal("5RS-853-EL-G6 \"ROBODUE\" 2 3/4\"BS", lockset.Catalog);
            Assert.Equal("RHR", lockset.Handing);
            Assert.Equal("Electrified Mortise Lockset", lockset.GenericName);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
