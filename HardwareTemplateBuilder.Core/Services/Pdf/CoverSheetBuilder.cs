using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Drawing;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Generates a professionally formatted cover sheet PDF using QuestPDF.
/// The cover sheet includes a header block (job metadata) and a body table
/// (one row per hardware item). Multi-page overflow is handled automatically.
/// Page 1 shows full job metadata (in the content area via ShowOnce); subsequent
/// pages show only the job number (in the repeating page header).
/// Each hardware item row is kept together on a single page.
/// </summary>
public class CoverSheetBuilder
{
    /// <summary>Dark blue header colour used for table header cells.</summary>
    private static readonly string HeaderBackground = "#1a3a5c";

    /// <summary>Light alternating row colour.</summary>
    private static readonly string AltRowBackground = "#eef3f8";

    static CoverSheetBuilder()
    {
        // Required by QuestPDF before any document generation.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// Generates the cover sheet for <paramref name="data"/> and saves it to
    /// <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="data">All metadata and hardware rows needed for the cover sheet.</param>
    /// <param name="outputPath">Full path for the output PDF file.</param>
    /// <param name="fillableTemplateBox">
    /// When <c>true</c>, the Page # and Remarks columns are omitted, the Template # column is
    /// widened, and each row renders a blank bordered box in place of pre-filled template
    /// numbers so the sheet can be filled in by hand. Used for standalone per-manufacturer
    /// cover sheets.
    /// </param>
    /// <returns>The <paramref name="outputPath"/> value for method chaining.</returns>
    public string Build(CoverSheetData data, string outputPath, bool fillableTemplateBox = false)
    {
        if (fillableTemplateBox)
            return BuildFillable(data, outputPath);

        EnsureDirectory(outputPath);

        var fontFamily = OperatingSystem.IsWindows() ? "Arial" : "Liberation Sans";

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(0.75f, Unit.Inch);
                page.DefaultTextStyle(style => style.FontFamily(fontFamily).FontSize(9));

                // ----- Page header — repeats identically on every page -----
                // Contains only the compact job identifier. Column headers are NOT placed
                // here because page.Header() always renders above page.Content(), which
                // would put the column names above the full metadata block on page 1.
                // Instead, column headers live in table.Header() inside the content so
                // they repeat per page while appearing below the metadata.
                page.Header().Column(header =>
                {
                    header.Item()
                            .Row(row =>
                            {
                                row.RelativeItem()
                                    .AlignLeft()
                                    .Text("Unified Door and Hardware")
                                    .FontSize(16)
                                    .Bold()
                                    .FontColor("#1a3a5c");
                                
                                row.RelativeItem()
                                    .AlignRight()
                                    .Text(t =>
                                    {
                                        t.Span("Job: ").Bold().FontSize(16).FontColor("#1a3a5c");
                                        t.Span(data.JobNumber).Bold().FontSize(16).FontColor("#1a3a5c");
                                    });
                            });
                            
                    
                    header.Item().LineHorizontal(1).LineColor("#1a3a5c");
                   
                    //header.Item()
                    //    .PaddingBottom(2)
                    //    .Text(t =>
                    //    {
                    //        t.Span("Job: ").Bold().FontSize(11).FontColor("#1a3a5c");
                    //        t.Span(data.JobNumber).Bold().FontSize(11).FontColor("#1a3a5c");
                    //    });
                                   
                });

                // ----- Content -----
                page.Content().PaddingTop(6).Column(col =>
                {
                    // Full metadata block — rendered only on the first content page.
                    // ShowOnce() works in page.Content() because content is a single
                    // continuous flow; the element collapses to zero height on pages 2+.
                    col.Item().ShowOnce().Column(meta =>
                    {
                        meta.Item().PaddingTop(10).Table(tbl =>
                        {
                            tbl.ColumnsDefinition(cols =>
                            {
                                cols.RelativeColumn(1); // label
                                cols.RelativeColumn(3); // value
                                cols.RelativeColumn(1); // label (right column)
                                cols.RelativeColumn(3); // value
                            });

                            void MetaCell(string text, bool bold = false)
                            {
                                var cell = tbl.Cell().PaddingVertical(2).PaddingHorizontal(4);
                                if (bold) cell.Text(text).Bold();
                                else cell.Text(text);
                            }

                            //MetaCell("Job #:", bold: true); MetaCell(data.JobNumber);
                            MetaCell("Job Name:", bold: true); MetaCell(data.JobName);
                            //tbl.Cell().ColumnSpan(4).PaddingVertical(1).Text(string.Empty); // Blank row

                            MetaCell("Customer:", bold: true); MetaCell(data.CustomerName);
                            MetaCell("PM:", bold: true); MetaCell(data.ProjectManagerName);
                            //tbl.Cell().ColumnSpan(4).PaddingVertical(1).Text(string.Empty); // Blank row

                            MetaCell("Date:", bold: true);
                            MetaCell(data.DateCreated.ToString("MMMM d, yyyy"));
                            MetaCell("Prep By:", bold: true);
                            MetaCell(data.PreparedBy);
                            tbl.Cell().ColumnSpan(2).Text(string.Empty);
                            tbl.Cell().ColumnSpan(4).Text(string.Empty);
                        });

                        meta.Item().PaddingTop(4).LineHorizontal(1).LineColor("#1a3a5c");
                        meta.Item().Height(6);
                    });

                    // Hardware table — table.Header() repeats the column name row at the
                    // top of every page the table overflows onto.
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(cols =>
                        {
                            cols.RelativeColumn(2); // Manufacturer
                            cols.RelativeColumn(2); // Hardware Type
                            cols.RelativeColumn(2); // Hardware Description
                            cols.RelativeColumn(2); // Template #
                            cols.RelativeColumn(1); // Page #
                            cols.RelativeColumn(2); // Remarks
                        });

                        table.Header(h =>
                        {
                            void HeaderCell(string text) =>
                                h.Cell()
                                    .Background(HeaderBackground)
                                    .PaddingVertical(5)
                                    .PaddingHorizontal(4)
                                    .Text(text)
                                    .FontColor(Colors.White)
                                    .Bold();

                            HeaderCell("Manufacturer");
                            HeaderCell("Hardware Type");
                            HeaderCell("Hardware Description");
                            HeaderCell("Template #");
                            HeaderCell("Page #");
                            HeaderCell("Remarks");
                        });

                        int rowIndex = 0;
                        foreach (var row in data.Rows)
                        {
                            if (row.IsGroupSeparator)
                            {
                                for (int c = 0; c < 6; c++)
                                    table.Cell().PaddingVertical(5).Text(string.Empty);
                                continue;
                            }

                            var bg = (rowIndex % 2 == 1) ? AltRowBackground : "#ffffff";
                            rowIndex++;

                            void DataCell(string? text) =>
                                table.Cell()
                                    .ShowEntire()
                                    .Background(bg)
                                    .BorderBottom(1)
                                    .BorderColor("#d0d8e4")
                                    .PaddingVertical(4)
                                    .PaddingHorizontal(4)
                                    .Text(text ?? string.Empty);

                            DataCell(row.Manufacturer);
                            DataCell(row.HardwareType);
                            DataCell(row.HardwareDescription);
                            DataCell(row.TemplateNumbers);
                            DataCell(row.PageNumbers);
                            DataCell(row.Remarks);
                        }
                    });
                });

                // ----- Footer -----
                page.Footer()
                    .AlignCenter()
                    .Text(x =>
                    {
                        x.Span("Cover Sheet — Page ").FontSize(8).FontColor("#666666");
                        x.CurrentPageNumber().FontSize(8).FontColor("#666666");
                        x.Span(" of ").FontSize(8).FontColor("#666666");
                        x.TotalPages().FontSize(8).FontColor("#666666");
                    });
            });
        }).GeneratePdf(outputPath);

        return outputPath;
    }

    /// <summary>
    /// Returns the number of pages in the PDF at <paramref name="pdfPath"/> by
    /// opening it with PDFsharp.
    /// </summary>
    /// <param name="pdfPath">Full path to a PDF file.</param>
    /// <returns>The page count.</returns>
    public static int GetPageCount(string pdfPath)
    {
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    /// <summary>
    /// Renders a standalone cover sheet directly with PdfSharp (bypassing QuestPDF) so that the
    /// Template # column can contain real, typeable AcroForm text field widgets rather than
    /// static text. Page # and Remarks columns are omitted and the Template # column is widened
    /// to make room for the fillable box.
    /// </summary>
    private string BuildFillable(CoverSheetData data, string outputPath)
    {
        EnsureDirectory(outputPath);

        const double pageWidth = 612;   // 8.5in
        const double pageHeight = 792;  // 11in
        const double margin = 54;       // 0.75in
        const double contentWidth = pageWidth - margin * 2;

        const double colManufacturer = 100;
        const double colType = 100;
        const double colDescription = 100;
        const double colTemplate = contentWidth - colManufacturer - colType - colDescription;

        const double compactHeaderHeight = 30;
        const double metadataHeight = 90;
        const double columnHeaderHeight = 24;
        const double rowHeight = 34;
        const double footerHeight = 20;

        var fontFamily = OperatingSystem.IsWindows() ? "Arial" : "Liberation Sans";
        var titleFont = new XFont(fontFamily, 14, XFontStyleEx.Bold);
        var labelFont = new XFont(fontFamily, 9, XFontStyleEx.Bold);
        var valueFont = new XFont(fontFamily, 9, XFontStyleEx.Regular);
        var headerCellFont = new XFont(fontFamily, 9, XFontStyleEx.Bold);
        var cellFont = new XFont(fontFamily, 9, XFontStyleEx.Regular);
        var footerFont = new XFont(fontFamily, 8, XFontStyleEx.Regular);

        var headerBg = XColor.FromArgb(0x1a, 0x3a, 0x5c);
        var altRowBg = XColor.FromArgb(0xee, 0xf3, 0xf8);
        var borderColor = XColor.FromArgb(0xd0, 0xd8, 0xe4);
        var fieldBorderColor = XColor.FromArgb(0x8a, 0x97, 0xa8);
        var footerColor = XColor.FromArgb(0x66, 0x66, 0x66);

        var firstPageCapacity = Math.Max(1, (int)((pageHeight - margin * 2 - compactHeaderHeight - metadataHeight - columnHeaderHeight - footerHeight) / rowHeight));
        var otherPageCapacity = Math.Max(1, (int)((pageHeight - margin * 2 - compactHeaderHeight - columnHeaderHeight - footerHeight) / rowHeight));

        var pages = new List<List<CoverSheetRow>>();
        var remaining = new Queue<CoverSheetRow>(data.Rows);
        var isFirstPage = true;
        while (remaining.Count > 0)
        {
            var capacity = isFirstPage ? firstPageCapacity : otherPageCapacity;
            var pageRows = new List<CoverSheetRow>();
            while (pageRows.Count < capacity && remaining.Count > 0)
                pageRows.Add(remaining.Dequeue());
            pages.Add(pageRows);
            isFirstPage = false;
        }
        if (pages.Count == 0)
            pages.Add(new List<CoverSheetRow>());

        using var document = new PdfDocument();

        // ----- Shared AcroForm scaffolding (one per document) -----
        var catalog = document.Internals.Catalog;
        var acroForm = new PdfDictionary(document);
        document.Internals.AddObject(acroForm);

        var helvFont = new PdfDictionary(document);
        helvFont.Elements.SetName("/Type", "/Font");
        helvFont.Elements.SetName("/Subtype", "/Type1");
        helvFont.Elements.SetName("/BaseFont", "/Helvetica");
        helvFont.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        document.Internals.AddObject(helvFont);

        var defaultResources = new PdfDictionary(document);
        document.Internals.AddObject(defaultResources);
        defaultResources.Elements.SetValue("/Helv", PdfInternals.GetReference(helvFont));

        acroForm.Elements.SetValue("/DR", PdfInternals.GetReference(defaultResources));
        acroForm.Elements.SetBoolean("/NeedAppearances", true);

        var fieldsArray = new PdfArray(document);
        document.Internals.AddObject(fieldsArray);
        acroForm.Elements.SetValue("/Fields", PdfInternals.GetReference(fieldsArray));
        catalog.Elements.SetValue("/AcroForm", PdfInternals.GetReference(acroForm));

        var fieldCounter = 0;

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(pageWidth);
            page.Height = XUnit.FromPoint(pageHeight);

            using var gfx = XGraphics.FromPdfPage(page);

            var y = margin;

            // ----- Compact repeating header -----
            gfx.DrawString("Unified Door and Hardware", titleFont, new XSolidBrush(headerBg), margin, y + 14);
            var jobLabel = $"Job: {data.JobNumber}";
            var jobSize = gfx.MeasureString(jobLabel, titleFont);
            gfx.DrawString(jobLabel, titleFont, new XSolidBrush(headerBg), margin + contentWidth - jobSize.Width, y + 14);
            y += 20;
            gfx.DrawLine(new XPen(headerBg, 1), margin, y, margin + contentWidth, y);
            y += 8;

            // ----- Full metadata block (page 1 only) -----
            if (pageIndex == 0)
            {
                void MetaRow(double rowY, string label1, string value1, string label2, string value2)
                {
                    gfx.DrawString(label1, labelFont, XBrushes.Black, margin, rowY);
                    gfx.DrawString(value1, valueFont, XBrushes.Black, margin + 65, rowY);
                    if (!string.IsNullOrEmpty(label2))
                    {
                        gfx.DrawString(label2, labelFont, XBrushes.Black, margin + contentWidth / 2, rowY);
                        gfx.DrawString(value2, valueFont, XBrushes.Black, margin + contentWidth / 2 + 65, rowY);
                    }
                }

                MetaRow(y + 9,  "Job Name:", data.JobName, "Customer:", data.CustomerName);
                MetaRow(y + 29, "PM:",       data.ProjectManagerName, "Date:", data.DateCreated.ToString("MMMM d, yyyy"));
                MetaRow(y + 49, "Prep By:",  data.PreparedBy, string.Empty, string.Empty);

                y += 64;
                gfx.DrawLine(new XPen(headerBg, 1), margin, y, margin + contentWidth, y);
                y += 6;
            }

            // ----- Column header row -----
            var x = margin;
            void HeaderCell(double width, string text)
            {
                gfx.DrawRectangle(new XSolidBrush(headerBg), x, y, width, columnHeaderHeight);
                gfx.DrawString(text, headerCellFont, XBrushes.White, new XRect(x + 4, y, width - 8, columnHeaderHeight), XStringFormats.CenterLeft);
                x += width;
            }
            HeaderCell(colManufacturer, "Manufacturer");
            HeaderCell(colType, "Hardware Type");
            HeaderCell(colDescription, "Hardware Description");
            HeaderCell(colTemplate, "Template #");
            y += columnHeaderHeight;

            // ----- Data rows -----
            var pageRows = pages[pageIndex];
            for (var r = 0; r < pageRows.Count; r++)
            {
                var row = pageRows[r];
                XBrush rowBrush = (r % 2 == 1) ? new XSolidBrush(altRowBg) : XBrushes.White;

                x = margin;
                void DataCell(double width, string? text)
                {
                    gfx.DrawRectangle(rowBrush, x, y, width, rowHeight);
                    if (!string.IsNullOrEmpty(text))
                        gfx.DrawString(text, cellFont, XBrushes.Black, new XRect(x + 4, y, width - 8, rowHeight), XStringFormats.CenterLeft);
                    gfx.DrawLine(new XPen(borderColor, 1), x, y + rowHeight, x + width, y + rowHeight);
                    x += width;
                }

                if (!row.IsGroupSeparator)
                {
                    DataCell(colManufacturer, row.Manufacturer);
                    DataCell(colType, row.HardwareType);
                    DataCell(colDescription, row.HardwareDescription);

                    gfx.DrawRectangle(rowBrush, x, y, colTemplate, rowHeight);
                    gfx.DrawLine(new XPen(borderColor, 1), x, y + rowHeight, x + colTemplate, y + rowHeight);

                    const double fieldPadding = 3;
                    var fieldRectTopDown = new XRect(
                        x + fieldPadding, y + fieldPadding,
                        colTemplate - fieldPadding * 2, rowHeight - fieldPadding * 2);
                    gfx.DrawRectangle(new XPen(fieldBorderColor, 0.75), fieldRectTopDown);

                    AddTemplateTextField(document, fieldsArray, page, fieldRectTopDown, pageHeight, $"TemplateNumbers_{fieldCounter++}");
                }

                y += rowHeight;
            }

            // ----- Footer -----
            var footerText = $"Cover Sheet — Page {pageIndex + 1} of {pages.Count}";
            var footerSize = gfx.MeasureString(footerText, footerFont);
            gfx.DrawString(footerText, footerFont, new XSolidBrush(footerColor),
                margin + (contentWidth - footerSize.Width) / 2, pageHeight - margin + 12);
        }

        document.Save(outputPath);
        return outputPath;
    }

    /// <summary>
    /// Adds a blank, typeable AcroForm text field widget to <paramref name="page"/> at
    /// <paramref name="rectTopDown"/> (expressed in PdfSharp's top-down drawing coordinates,
    /// converted here to native bottom-up PDF user space) and registers it on the shared
    /// <see cref="PdfArray"/> of form fields.
    /// </summary>
    private static void AddTemplateTextField(
        PdfDocument document, PdfArray fieldsArray, PdfPage page, XRect rectTopDown, double pageHeight, string fieldName)
    {
        var pdfRect = new PdfSharp.Pdf.PdfRectangle(new XRect(
            rectTopDown.X,
            pageHeight - rectTopDown.Y - rectTopDown.Height,
            rectTopDown.Width,
            rectTopDown.Height));

        var field = new PdfDictionary(document);
        document.Internals.AddObject(field);
        field.Elements.SetName("/FT", "/Tx");
        field.Elements.SetName("/Subtype", "/Widget");
        field.Elements.SetString("/T", fieldName);
        field.Elements.SetString("/V", string.Empty);
        field.Elements.SetString("/DA", "/Helv 9 Tf 0 g");
        field.Elements.SetRectangle("/Rect", pdfRect);
        field.Elements.SetInteger("/F", 4); // Print flag
        field.Elements.SetValue("/P", PdfInternals.GetReference(page));

        fieldsArray.Elements.Add(PdfInternals.GetReference(field));

        var annotsArray = page.Elements.GetArray("/Annots");
        if (annotsArray == null)
        {
            annotsArray = new PdfArray(document);
            document.Internals.AddObject(annotsArray);
            page.Elements.SetValue("/Annots", PdfInternals.GetReference(annotsArray));
        }
        annotsArray.Elements.Add(PdfInternals.GetReference(field));
    }

    /// <summary>Creates the directory for <paramref name="filePath"/> if it does not exist.</summary>
    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
