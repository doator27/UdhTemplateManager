using HardwareTemplateBuilder.Core.Services.Import;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace HardwareTemplateBuilder.Tests;

/// <summary>Tests for the hardware PDF parsers (text excerpts from real schedules/costing reports).</summary>
public class PdfImportParserTests
{
    private static IReadOnlyList<IReadOnlyList<string>> Load(string name) =>
        new[] { (IReadOnlyList<string>)File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", "PdfImport", name)) };

    /// <summary>Loads a multi-page fixture ("=== PAGE n" separators).</summary>
    private static IReadOnlyList<IReadOnlyList<string>> LoadPages(string name)
    {
        var pages = new List<List<string>>();
        foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "TestData", "PdfImport", name)))
        {
            if (line.StartsWith("=== PAGE", StringComparison.Ordinal)) pages.Add(new List<string>());
            else if (pages.Count > 0) pages[^1].Add(line);
        }
        return pages;
    }

    [Fact]
    public void MachiningSheet_ReadsTheHardwareSection()
    {
        var r = HardwarePdfParser.Parse(LoadPages("machining_sheet.txt"));

        Assert.Contains("Door machining sheet", r.Formats);
        Assert.Equal(14, r.Lines.Count); // 3 + 3 + 3 items, then 5 on the last page

        var hinge = r.Lines[0];
        Assert.Equal("Ives", hinge.ManufacturerName);
        Assert.Equal("5BB1 4 1/2\" x 4 1/2\" NRP BAA", hinge.Catalog);
        Assert.Equal("630", hinge.Finish);
        Assert.Equal("Hinge", hinge.GenericName);
        Assert.Equal(6, hinge.Quantity);                     // 3 per door x 2 openings
        Assert.Equal("Type NL1 (SNAP): E100C, U100C", hinge.SetName);

        var lockset = r.Lines[1];
        Assert.Equal("Schlage", lockset.ManufacturerName);
        Assert.Equal("ND10 RHO BAA", lockset.Catalog);
        Assert.Equal("626", lockset.Finish);
        Assert.Equal("Lock", lockset.GenericName);

        var closer = r.Lines[2];
        Assert.Equal("LCN", closer.ManufacturerName);
        Assert.Equal("4040XP RWPA TBSRT", closer.Catalog);     // finish, handing and door thickness out
        Assert.Equal("LH", closer.Handing);
        Assert.Equal("689", closer.Finish);
        Assert.Equal("Closer", closer.GenericName);

        var last = r.Lines.Where(l => l.Page == 4).ToList();
        Assert.Equal(5, last.Count);
        var exit = Assert.Single(last, l => l.ManufacturerName == "Von Duprin");
        Assert.StartsWith("RX-98-NL-F-", exit.Catalog);
        Assert.Equal("Rim Exit Device", exit.GenericName);
        Assert.Equal(1, exit.Quantity);
        Assert.Equal("Type M: A220", exit.SetName);
        Assert.Contains(last, l => l.ManufacturerName == "Schlage" && l.Catalog == "M490P 450/490-ATS/LED");
        Assert.Contains(last, l => l.Catalog == "5BB1 4 1/2\" x 4 1/2\" TW4 CON BAA" && l.Quantity == 1);
        // Nothing from the drawing (dimensions) or the notes below the list.
        Assert.DoesNotContain(r.Lines, l => l.Catalog.Contains("Pioneer") || l.Catalog.Contains("8/3"));
    }

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
    public void QtyFirstSchedule_HardwareSet()
    {
        var r = HardwarePdfParser.Parse(LoadPages("qtyfirst_set.txt"));

        Assert.Contains("Hardware schedule (quantity first)", r.Formats);
        Assert.Equal(14, r.Lines.Count);
        var hinge = r.Lines.Single(l => l.GenericName == "Standard Hinge");
        Assert.Equal("BEST", hinge.ManufacturerName);
        Assert.Equal("FBB179 (4 1/2\" x 4 1/2\")", hinge.Catalog);
        Assert.Equal("US26D", hinge.Finish);
        Assert.Equal(171, hinge.Quantity);
        Assert.Equal("U01 - ENTRY", hinge.SetName);

        var lockset = r.Lines.Single(l => l.GenericName == "LX-M Lockset");
        Assert.Equal("dormakaba Architectural", lockset.ManufacturerName);
        Assert.Equal("M002KSCBPSTCB-11F", lockset.Catalog);
        var closer = r.Lines.Single(l => l.GenericName == "Concealed Closer");
        Assert.Equal("dormakaba", closer.ManufacturerName);
        Assert.Equal("ITS96 INV 13 BRKTMD", closer.Catalog);
        // No finish column: a trailing finish code is taken out of the callout.
        var viewer = r.Lines.Single(l => l.GenericName == "UL Viewer");
        Assert.Equal("976U", viewer.Catalog);
        Assert.Equal("626", viewer.Finish);
        // Notes under an item ("***CONCEALED") are not part of the callout.
        Assert.DoesNotContain(r.Lines, l => l.Catalog.Contains('*'));
    }

    [Fact]
    public void QtyFirstSchedule_Groups()
    {
        var r = HardwarePdfParser.Parse(LoadPages("qtyfirst_group.txt"));

        Assert.Equal(new[] { "E1", "E1.1", "01.0B1", "13" }, r.Lines.Select(l => l.SetName).Distinct());
        Assert.Equal(15, r.Lines.Count(l => l.SetName == "E1"));
        Assert.Equal(8, r.Lines.Count(l => l.SetName == "01.0B1"));
        Assert.Equal(8, r.Lines.Count(l => l.SetName == "13"));

        var locks = r.Lines.Where(l => l.SetName == "E1" && l.GenericName == "Electric Lock").ToList();
        Assert.All(locks, l => Assert.Equal("RX WBS-8271-24V LL LC", l.Catalog));
        Assert.Equal(new[] { "LH", "RH" }, locks.Select(l => l.Handing));
        Assert.Equal(new[] { 4, 6 }, locks.Select(l => l.Quantity));

        var cyl = r.Lines.First(l => l.GenericName == "Permanent Cylinder");
        Assert.Equal("By Others", cyl.ManufacturerName);
        var closer = r.Lines.First(l => l.SetName == "E1" && l.GenericName == "Surface Closer");
        Assert.Equal("4040XP RWPA", closer.Catalog);

        // "003.1 - Standard Hinge": the item number is dropped from the name.
        var hinge = r.Lines.First(l => l.SetName == "01.0B1");
        Assert.Equal("Standard Hinge", hinge.GenericName);
        Assert.Equal("McKinney", hinge.ManufacturerName);
        Assert.Equal("MPB91 4 1/2\" x 4 1/2\" NRP", hinge.Catalog);
        Assert.Equal(3, hinge.Quantity);
        var lowProfile = r.Lines.Single(l => l.SetName == "01.0B1" && l.GenericName == "Pull Side Mount Low Profile Closer");
        Assert.Equal("Norton Rixson", lowProfile.ManufacturerName);
        Assert.Equal("8541ST", lowProfile.Catalog);
        var storeroom = r.Lines.Single(l => l.SetName == "01.0B1" && l.GenericName == "Storeroom Function");
        Assert.Equal("MO 8840FL MK CMK", storeroom.Catalog);
        Assert.Equal("LHR", storeroom.Handing);
        // "890 - HW/BO   Note Card Reader by Others" is a note, not hardware.
        Assert.DoesNotContain(r.Lines, l => l.GenericName == "HW/BO");

        // The name ran into the callout: split at the manufacturer.
        var bolt = r.Lines.Single(l => l.GenericName == "Manual Flush Bolt HMD");
        Assert.Equal("Rockwood", bolt.ManufacturerName);
        Assert.Equal("555", bolt.Catalog);
        Assert.Equal(28, bolt.Quantity);
        // Door lines ("1  1 Pair of doors ...") aren't items.
        Assert.DoesNotContain(r.Lines, l => l.Catalog.Contains("Corridor"));
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
    public void Costing_NamesCodesFromTheCostSummary()
    {
        var r = HardwarePdfParser.Parse(LoadPages("costing_full.txt"));

        Assert.Contains("Component costing", r.Formats);
        Assert.Equal("Accurate Lock", r.ManufacturerCodes["AC01"]);
        Assert.Equal("SIMONSWERK North America", r.ManufacturerCodes["S701"]);
        Assert.Equal("Hafele America Co.", r.ManufacturerCodes["HF01"]);
        Assert.Equal("Rajack Hardware", r.ManufacturerCodes["RA01"]);
        Assert.Equal("Marks Locks", r.ManufacturerCodes["MK01"]);
        Assert.Equal("LCN Closers", r.ManufacturerCodes["LC"]);
        Assert.Equal("Various", r.ManufacturerCodes["VA01"]);
        Assert.All(r.Lines.Where(l => l.ManufacturerCode == "AC01"), l => Assert.Equal("Accurate Lock", l.ManufacturerName));

        // Every priced line is read (the summary pages add none), finishes come off the locks.
        Assert.Equal(60817.00m, r.Lines.Sum(l => l.ExtendedCost ?? 0));
        var privacy = r.Lines.Where(l => l.Catalog.StartsWith("9539ML.212", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, privacy.Count);
        Assert.Single(privacy.Select(l => ImportText.Key(l.Catalog)).Distinct());
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
    public void LetterWords_SplitOnGapsWithoutSpaces()
    {
        // "Concealed Closer 2033" written glyph by glyph with no space characters (like GemBox).
        var letters = new List<PdfLetter>();
        double x = 71;
        foreach (var word in new[] { "Concealed", "Closer", "2033" })
        {
            foreach (var ch in word)
            {
                letters.Add(new PdfLetter(x, x + 5, 100, 9, ch.ToString()));
                x += 5;
            }
            x += 2.5; // the only thing that separates the words
        }
        letters.Add(new PdfLetter(183, 188, 100.2, 9, "2"));
        letters.Add(new PdfLetter(40, 45, 120, 9, "L"));
        letters.Add(new PdfLetter(45, 50, 120, 9, "C"));
        letters.Add(new PdfLetter(50, 52, 120, 9, " "));
        letters.Add(new PdfLetter(52, 57, 120, 9, "X"));

        var words = PdfLetterWords.Group(letters);

        Assert.Equal(new[] { "Concealed", "Closer", "2033", "2", "LC", "X" }, words.Select(w => w.Text));
        Assert.Equal(71, words[0].Left);
    }

    [Fact]
    public async Task TextReader_SplitsWordsInPdfsWithoutSpaces()
    {
        if (!OperatingSystem.IsWindows()) return; // PDFsharp needs Windows fonts to draw the test PDF.

        var path = Path.Combine(Path.GetTempPath(), $"htb_nospace_{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                using var gfx = XGraphics.FromPdfPage(page);
                var font = new XFont("Arial", 9);
                // Every word drawn on its own, 2.5pt apart, so the PDF has no space characters.
                void Words(double x, double y, string line)
                {
                    foreach (var w in line.Split(' '))
                    {
                        gfx.DrawString(w, font, XBrushes.Black, x, y);
                        x += gfx.MeasureString(w, font).Width + 2.5;
                    }
                }
                Words(39, 60, "Mfg"); Words(71, 60, "Product Description"); Words(198, 60, "Price"); Words(502, 60, "Qty"); Words(551, 60, "Cost");
                Words(77, 72, "Cost Group: Hardware - Closers");
                Words(39, 84, "LC"); Words(71, 84, "Concealed Closer 2033"); Words(183, 84, "2,871.00"); Words(463, 84, "735.69"); Words(515, 84, "1"); Words(542, 84, "735.69");
                Words(71, 96, "BUMP 639");
                Words(81, 108, "Openings: CUSTOM.1");
                doc.Save(path);
            }

            var text = await HardwarePdfTextReader.ReadAsync(path, null, null, CancellationToken.None);
            Assert.Contains(text.Pages[0], l => l.Contains("Product Description", StringComparison.Ordinal));
            var r = HardwarePdfParser.Parse(text.Pages.Select(p => (IReadOnlyList<string>)p).ToList());

            Assert.Contains("Component costing", r.Formats);
            var closer = Assert.Single(r.Lines);
            Assert.Equal("LC", closer.ManufacturerCode);
            Assert.Equal("Concealed Closer", closer.GenericName);
            Assert.Equal("2033 BUMP", closer.Catalog);
            Assert.Equal("CUSTOM.1", closer.SetName);
        }
        finally
        {
            File.Delete(path);
        }
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
