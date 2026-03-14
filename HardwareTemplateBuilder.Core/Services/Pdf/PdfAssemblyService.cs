using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Orchestrates the full PDF assembly pipeline for a job package:
/// template sorting → file acquisition → page extraction/rotation →
/// body merge → cover sheet → final merge → page numbering.
/// </summary>
public class PdfAssemblyService
{
    private readonly TemplateSorter _sorter;
    private readonly FileAcquirer _acquirer;
    private readonly PageRangeParser _pageRangeParser;
    private readonly PageExtractor _extractor;
    private readonly PageRotator _rotator;
    private readonly PdfMerger _merger;
    private readonly CoverSheetBuilder _coverSheetBuilder;
    private readonly PageNumberer _numberer;

    /// <summary>
    /// Initializes a new <see cref="PdfAssemblyService"/> with all required PDF services.
    /// </summary>
    public PdfAssemblyService(
        TemplateSorter sorter,
        FileAcquirer acquirer,
        PageRangeParser pageRangeParser,
        PageExtractor extractor,
        PageRotator rotator,
        PdfMerger merger,
        CoverSheetBuilder coverSheetBuilder,
        PageNumberer numberer)
    {
        _sorter = sorter;
        _acquirer = acquirer;
        _pageRangeParser = pageRangeParser;
        _extractor = extractor;
        _rotator = rotator;
        _merger = merger;
        _coverSheetBuilder = coverSheetBuilder;
        _numberer = numberer;
    }

    /// <summary>
    /// Assembles the full PDF package for the job described by <paramref name="request"/>.
    /// </summary>
    /// <param name="request">Job metadata and hardware/template data.</param>
    /// <param name="progress">
    /// Optional progress reporter; receives per-step status strings.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// An <see cref="AssemblyResult"/> containing the output path and cover sheet page count.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the request contains no hardware items or all hardware items have no templates.
    /// </exception>
    public async Task<AssemblyResult> AssembleAsync(
        AssemblyRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Deduplicate: the same IndividualTemplate may be linked to multiple hardware items;
        // it should appear only once in the merged PDF.
        var allTemplates = request.Hardware
            .SelectMany(h => h.Templates)
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .ToList();

        if (allTemplates.Count == 0)
            throw new InvalidOperationException(
                "Cannot assemble a PDF package: no templates are linked to any hardware item.");

        var jobDir = Path.Combine(request.OutputDirectory, request.Job.JobNumber);
        var workDir = Path.Combine(jobDir, "work");
        Directory.CreateDirectory(workDir);

        // Build a reverse lookup: template ID → all hardware items that contain it
        // (a template might be shared across multiple items).
        var templateToItems = BuildTemplateToItemsMap(request.Hardware);

        // ----- Step 1: Sort templates -----
        progress?.Report("Sorting templates...");
        var sortedTemplates = _sorter.Sort(allTemplates, request.AllDescriptions);

        // ----- Step 2 & 3: Acquire, extract, and rotate each template -----
        var bodyPdfs = new List<string>();
        var snapshotInfos = new List<TemplateSnapshotInfo>();
        // Tracks which body pages (1-based) each hardware item occupies.
        var itemBodyPages = request.Hardware
            .ToDictionary(h => h.Item.Id, _ => new List<int>());
        int bodyPageCursor = 1;

        foreach (var template in sortedTemplates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report($"Acquiring {template.TemplateNumber}...");
            var acquiredPath = await _acquirer.AcquireAsync(template, jobDir);

            // Record snapshot info at acquisition time — before any processing.
            snapshotInfos.Add(new TemplateSnapshotInfo
            {
                IndividualTemplateId = template.Id,
                AcquiredFilePath     = acquiredPath,
                PagesToPrint         = template.PagesToPrint,
                PagesToRotate        = template.PagesToRotate,
                RotationDirection    = template.RotationDirection
            });

            progress?.Report($"Processing {template.TemplateNumber}...");
            var pageNumbers = _pageRangeParser.Parse(template.PagesToPrint);

            // Extract the specified pages.
            var extractedPath = Path.Combine(workDir, $"ex_{template.Id}.pdf");
            _extractor.Extract(acquiredPath, pageNumbers, extractedPath);

            // Rotate pages if required.
            string processedPath = extractedPath;
            if (!string.IsNullOrWhiteSpace(template.PagesToRotate))
            {
                var rotateOriginal = _pageRangeParser.Parse(template.PagesToRotate);

                // Map original page numbers to 1-based indices within the extracted PDF.
                var indexMap = pageNumbers
                    .Select((p, i) => (Original: p, ExtractedIndex: i + 1))
                    .ToDictionary(x => x.Original, x => x.ExtractedIndex);

                var rotateExtracted = rotateOriginal
                    .Where(p => indexMap.ContainsKey(p))
                    .Select(p => indexMap[p])
                    .ToList();

                if (rotateExtracted.Count > 0)
                {
                    processedPath = Path.Combine(workDir, $"rot_{template.Id}.pdf");
                    _rotator.Rotate(extractedPath, rotateExtracted, template.RotationDirection, processedPath);
                }
            }

            bodyPdfs.Add(processedPath);

            // Record which body pages this template occupies, per owning hardware item.
            if (templateToItems.TryGetValue(template.Id, out var owners))
            {
                foreach (var item in owners)
                {
                    if (itemBodyPages.TryGetValue(item.Id, out var pages))
                    {
                        for (int p = 0; p < pageNumbers.Count; p++)
                            pages.Add(bodyPageCursor + p);
                    }
                }
            }

            bodyPageCursor += pageNumbers.Count;
        }

        // ----- Step 4: Merge body PDF -----
        progress?.Report("Merging body PDF...");
        var bodyPdfPath = Path.Combine(workDir, "body.pdf");
        _merger.Merge(bodyPdfs, bodyPdfPath);

        // ----- Step 5: Build cover sheet -----
        progress?.Report("Building cover sheet...");
        var coverSheetData = BuildCoverSheetData(request, itemBodyPages);
        var coverSheetPath = Path.Combine(workDir, "coversheet.pdf");
        _coverSheetBuilder.Build(coverSheetData, coverSheetPath);
        int coverPageCount = CoverSheetBuilder.GetPageCount(coverSheetPath);

        // ----- Step 6: Prepend cover sheet -----
        progress?.Report("Assembling final PDF...");
        var mergedPath = Path.Combine(workDir, "merged.pdf");
        _merger.Merge(new[] { coverSheetPath, bodyPdfPath }, mergedPath);

        // ----- Step 7: Stamp page numbers on body pages -----
        var finalPath = Path.Combine(jobDir, $"{request.Job.JobNumber}_templates.pdf");
        _numberer.StampPageNumbers(mergedPath, coverPageCount, finalPath);

        progress?.Report("Done.");
        return new AssemblyResult
        {
            OutputPath          = finalPath,
            CoverSheetPageCount = coverPageCount,
            TemplateSnapshots   = snapshotInfos.AsReadOnly()
        };
    }

    // ---------- Private helpers ----------

    /// <summary>
    /// Builds a <see cref="CoverSheetData"/> from the assembly request and the
    /// computed per-item body page lists.
    /// </summary>
    private static CoverSheetData BuildCoverSheetData(
        AssemblyRequest request,
        Dictionary<int, List<int>> itemBodyPages)
    {
        var rows = request.Hardware.Select(hwt =>
        {
            var item = hwt.Item;
            var pages = itemBodyPages.TryGetValue(item.Id, out var p) ? p : new List<int>();
            var templateNumbers = string.Join(", ", hwt.Templates.Select(t => t.TemplateNumber));
            var pageRange = FormatPageList(pages);

            return new CoverSheetRow
            {
                Manufacturer = item.Manufacturer?.ManufacturerName ?? string.Empty,
                HardwareType = item.Description?.DescriptionText ?? string.Empty,
                HardwareDescription = !string.IsNullOrWhiteSpace(hwt.CustomDescription)
                    ? hwt.CustomDescription
                    : item.ModelNumber,
                TemplateNumbers = templateNumbers,
                PageNumbers = pageRange,
                Remarks = item.Remarks
            };
        }).ToList();

        return new CoverSheetData
        {
            JobNumber = request.Job.JobNumber,
            JobName = request.Job.JobName,
            CustomerName = request.Job.Customer?.CustomerName ?? string.Empty,
            ProjectManagerName = request.Job.ProjectManager?.ProjectManagerName ?? string.Empty,
            DateCreated = DateTime.Now,
            Rows = rows.AsReadOnly()
        };
    }

    /// <summary>
    /// Builds a reverse lookup from template ID to the list of
    /// <see cref="HardwareItem"/>s that contain it.
    /// </summary>
    private static Dictionary<int, List<HardwareItem>> BuildTemplateToItemsMap(
        IReadOnlyList<HardwareWithTemplates> hardware)
    {
        var map = new Dictionary<int, List<HardwareItem>>();
        foreach (var hwt in hardware)
        {
            foreach (var template in hwt.Templates)
            {
                if (!map.TryGetValue(template.Id, out var items))
                {
                    items = new List<HardwareItem>();
                    map[template.Id] = items;
                }
                items.Add(hwt.Item);
            }
        }
        return map;
    }

    /// <summary>
    /// Formats a sorted list of page numbers into a compact range string,
    /// e.g. [1, 2, 3, 5, 6] → "1-3, 5-6".
    /// </summary>
    private static string FormatPageList(IReadOnlyList<int> pages)
    {
        if (pages.Count == 0) return string.Empty;

        var sorted = pages.OrderBy(p => p).Distinct().ToList();
        var parts = new List<string>();
        int start = sorted[0];
        int prev = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == prev + 1)
            {
                prev = sorted[i];
            }
            else
            {
                parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
                start = sorted[i];
                prev = sorted[i];
            }
        }

        parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
        return string.Join(", ", parts);
    }
}
