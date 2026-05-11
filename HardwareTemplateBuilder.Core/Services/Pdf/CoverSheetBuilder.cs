using QuestPDF.Elements;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using PdfSharp.Pdf.IO;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Generates a professionally formatted cover sheet PDF using QuestPDF.
/// The cover sheet includes a header block (job metadata) and a body table
/// (one row per hardware item). Multi-page overflow is handled automatically.
/// Page 1 shows full job metadata; subsequent pages show only the job number.
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

                // IDynamicComponent renders the full metadata block on page 1 only
                // and just the job number on subsequent pages.
                // ShowOnce/SkipOnce do not work in page.Header() because the header
                // is a fresh independent render on each page with no cross-page state.
                page.Header().Dynamic(new PageHeaderComponent(data));

                // ----- Body: one Column item per row, each kept whole on a single page -----
                page.Content().PaddingTop(6).Column(col =>
                {
                    int rowIndex = 0;
                    foreach (var row in data.Rows)
                    {
                        if (row.IsGroupSeparator)
                        {
                            // Blank spacer between manufacturer groups.
                            col.Item().Height(10);
                            continue;
                        }

                        var bg = (rowIndex % 2 == 1) ? AltRowBackground : "#ffffff";
                        rowIndex++;

                        // ShowEntire() moves the entire row to the next page if it won't fit,
                        // preventing a single row's content from splitting across pages.
                        col.Item().ShowEntire().Row(dataRow =>
                        {
                            void DataCell(int weight, string? text) =>
                                dataRow.RelativeItem(weight)
                                    .Background(bg)
                                    .BorderBottom(1)
                                    .BorderColor("#d0d8e4")
                                    .PaddingVertical(4)
                                    .PaddingHorizontal(4)
                                    .Text(text ?? string.Empty);

                            DataCell(2, row.Manufacturer);
                            DataCell(2, row.HardwareType);
                            DataCell(2, row.HardwareDescription);
                            DataCell(2, row.TemplateNumbers);
                            DataCell(1, row.PageNumbers);
                            DataCell(2, row.Remarks);
                        });
                    }
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

    /// <summary>
    /// Renders the page header differently for page 1 (full job metadata) versus all
    /// subsequent pages (job number only). Uses <see cref="DynamicContext.PageNumber"/>
    /// so no state tracking is required.
    /// </summary>
    private sealed class PageHeaderComponent : IDynamicComponent
    {
        private readonly CoverSheetData _data;

        /// <summary>Initialises the component with the cover sheet data to render.</summary>
        public PageHeaderComponent(CoverSheetData data) => _data = data;

        /// <inheritdoc/>
        public DynamicComponentComposeResult Compose(DynamicContext context)
        {
            bool isFirstPage = context.PageNumber == 1;

            var content = context.CreateElement(container =>
                container.Column(col =>
                {
                    if (isFirstPage)
                    {
                        col.Item()
                            .AlignCenter()
                            .Text("Unified Door and Hardware Templates")
                            .FontSize(16)
                            .Bold()
                            .FontColor("#1a3a5c");

                        col.Item().PaddingTop(10).Table(meta =>
                        {
                            meta.ColumnsDefinition(cd =>
                            {
                                cd.RelativeColumn(1); // label
                                cd.RelativeColumn(3); // value
                                cd.RelativeColumn(1); // label (right column)
                                cd.RelativeColumn(3); // value
                            });

                            void MetaCell(string text, bool bold = false)
                            {
                                var cell = meta.Cell().PaddingVertical(2).PaddingHorizontal(4);
                                if (bold) cell.Text(text).Bold();
                                else cell.Text(text);
                            }

                            MetaCell("Job Number:", bold: true); MetaCell(_data.JobNumber);
                            MetaCell("Job Name:", bold: true);   MetaCell(_data.JobName);
                            MetaCell("Customer:", bold: true);        MetaCell(_data.CustomerName);
                            MetaCell("Project Manager:", bold: true); MetaCell(_data.ProjectManagerName);
                            MetaCell("Date Created:", bold: true);
                            MetaCell(_data.DateCreated.ToString("MMMM d, yyyy"));
                            MetaCell("Templates by:", bold: true);
                            MetaCell(_data.PreparedBy);
                            meta.Cell().ColumnSpan(2).Text(string.Empty);
                            meta.Cell().ColumnSpan(4).Text(string.Empty);
                        });

                        col.Item().PaddingTop(6).LineHorizontal(1).LineColor("#1a3a5c");
                    }
                    else
                    {
                        col.Item().Text(t =>
                        {
                            t.Span("Job: ").Bold().FontSize(11).FontColor("#1a3a5c");
                            t.Span(_data.JobNumber).Bold().FontSize(11).FontColor("#1a3a5c");
                        });
                        col.Item().PaddingTop(4).LineHorizontal(1).LineColor("#1a3a5c");
                    }

                    // Column header row — every page
                    col.Item().PaddingTop(4).Row(headerRow =>
                    {
                        void HeaderCell(int weight, string text) =>
                            headerRow.RelativeItem(weight)
                                .Background(HeaderBackground)
                                .PaddingVertical(5)
                                .PaddingHorizontal(4)
                                .Text(text)
                                .FontColor(Colors.White)
                                .Bold();

                        HeaderCell(2, "Manufacturer");
                        HeaderCell(2, "Hardware Type");
                        HeaderCell(2, "Hardware Description");
                        HeaderCell(2, "Template #");
                        HeaderCell(1, "Page #");
                        HeaderCell(2, "Remarks");
                    });
                })
            );

            return new DynamicComponentComposeResult
            {
                Content = (IElement)content,
                HasMoreContent = false
            };
        }
    }
}
