using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using PdfSharp.Pdf.IO;

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
    /// <returns>The <paramref name="outputPath"/> value for method chaining.</returns>
    public string Build(CoverSheetData data, string outputPath)
    {
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
                        .PaddingBottom(4)
                        .Text(t =>
                        {
                            t.Span("Job: ").Bold().FontSize(11).FontColor("#1a3a5c");
                            t.Span(data.JobNumber).Bold().FontSize(11).FontColor("#1a3a5c");
                        });

                    header.Item().LineHorizontal(1).LineColor("#1a3a5c");
                });

                // ----- Content -----
                page.Content().PaddingTop(6).Column(col =>
                {
                    // Full metadata block — rendered only on the first content page.
                    // ShowOnce() works in page.Content() because content is a single
                    // continuous flow; the element collapses to zero height on pages 2+.
                    col.Item().ShowOnce().Column(meta =>
                    {
                        meta.Item()
                            .AlignCenter()
                            .Text("Unified Door and Hardware Templates")
                            .FontSize(16)
                            .Bold()
                            .FontColor("#1a3a5c");

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

                            MetaCell("Job Number:", bold: true); MetaCell(data.JobNumber);
                            MetaCell("Job Name:", bold: true);   MetaCell(data.JobName);
                            MetaCell("Customer:", bold: true);        MetaCell(data.CustomerName);
                            MetaCell("Project Manager:", bold: true); MetaCell(data.ProjectManagerName);
                            MetaCell("Date Created:", bold: true);
                            MetaCell(data.DateCreated.ToString("MMMM d, yyyy"));
                            MetaCell("Templates by:", bold: true);
                            MetaCell(data.PreparedBy);
                            tbl.Cell().ColumnSpan(2).Text(string.Empty);
                            tbl.Cell().ColumnSpan(4).Text(string.Empty);
                        });

                        meta.Item().PaddingTop(8).LineHorizontal(1).LineColor("#1a3a5c");
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

    /// <summary>Creates the directory for <paramref name="filePath"/> if it does not exist.</summary>
    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
