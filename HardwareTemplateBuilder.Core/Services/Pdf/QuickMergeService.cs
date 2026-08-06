using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Generates a quick, cover-sheet-less merged PDF of every template linked to a single
/// <see cref="HardwareItem"/>. This is the shared pipeline behind both the in-app Template
/// Lookup view (<c>HardwareTemplateBuilder.App</c>) and the standalone Lookup app's
/// "Open Templates" button (<c>HardwareTemplateBuilder.Lookup</c>) — deliberately bypasses
/// <see cref="PdfAssemblyService"/> (no cover sheet, no <c>JobTemplateSnapshot</c> write).
/// Both call sites previously reimplemented this pipeline independently; it now lives here
/// once so the two apps can't drift apart.
/// </summary>
public class QuickMergeService
{
    private readonly AppDbContext _context;
    private readonly HttpClient _httpClient;

    /// <summary>Initializes a new <see cref="QuickMergeService"/>.</summary>
    /// <param name="context">Database context for reading templates and descriptions.</param>
    /// <param name="httpClient">HTTP client used to download templates with no local copy.</param>
    public QuickMergeService(AppDbContext context, HttpClient httpClient)
    {
        _context = context;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Builds the output file name for <paramref name="item"/>: <c>{ModelNumber}_templates.pdf</c>
    /// with invalid file-name characters replaced by underscores. Exposed so callers can compute
    /// the expected output path for an overwrite-confirmation prompt before generation starts.
    /// </summary>
    public static string BuildOutputFileName(HardwareItem item) =>
        $"{Sanitize(item.ModelNumber)}_templates.pdf";

    /// <summary>
    /// Loads every template linked to <paramref name="item"/>, sorts them, acquires/processes
    /// each PDF (extracting and rotating pages as configured), merges them into a single file,
    /// and saves it to <paramref name="saveLocation"/> as <c>{ModelNumber}_templates.pdf</c>.
    /// Runs the heavy work on the thread pool; <paramref name="progress"/> is called from that
    /// thread, so callers marshalling to a UI thread must do so themselves.
    /// </summary>
    /// <param name="item">The hardware item whose linked templates should be merged.</param>
    /// <param name="saveLocation">Destination directory for the merged output file.</param>
    /// <param name="workDirPrefix">
    /// Prefix for the temporary working directory name (e.g. <c>"htb"</c> or <c>"htb_lookup"</c>)
    /// — kept distinct per caller so concurrent runs from both apps never collide on the same
    /// temp folder.
    /// </param>
    /// <param name="progress">Optional progress reporter for per-template status messages.</param>
    /// <param name="ct">Token to cancel the operation between templates.</param>
    /// <returns>The full path to the merged output PDF.</returns>
    public async Task<string> GenerateAsync(
        HardwareItem item,
        string saveLocation,
        string workDirPrefix,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var templates = _context.HardwareItemTemplates
            .Include(hit => hit.IndividualTemplate)
                .ThenInclude(t => t.Manufacturer)
            .Include(hit => hit.IndividualTemplate)
                .ThenInclude(t => t.Description)
            .Where(hit => hit.HardwareItemId == item.Id)
            .Select(hit => hit.IndividualTemplate)
            .ToList();

        if (templates.Count == 0)
            throw new InvalidOperationException(
                "This hardware item has no linked templates. Link templates via the Hardware Items screen.");

        var allDescriptions = _context.Descriptions.ToDictionary(d => d.Id);
        var sortedTemplates = new TemplateSorter(new WeightTemplateSortStrategy())
            .Sort(templates, allDescriptions);

        // Run heavy PDF work on a thread-pool thread to keep the calling UI thread responsive.
        return await Task.Run(async () =>
        {
            var workDir = Path.Combine(
                Path.GetTempPath(), $"{workDirPrefix}_{item.Id}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);

            var acquirer  = new FileAcquirer(_httpClient);
            var parser    = new PageRangeParser();
            var extractor = new PageExtractor();
            var rotator   = new PageRotator();
            var merger    = new PdfMerger();

            var processedPdfs = new List<string>();

            foreach (var template in sortedTemplates)
            {
                ct.ThrowIfCancellationRequested();

                progress?.Report($"Acquiring {template.TemplateNumber}...");
                var acquired = await acquirer.AcquireAsync(template, workDir);

                progress?.Report($"Processing {template.TemplateNumber}...");
                var pageNumbers = parser.Parse(template.PagesToPrint);
                var extracted = Path.Combine(workDir, $"ex_{template.Id}.pdf");
                extractor.Extract(acquired, pageNumbers, extracted);

                string processed = extracted;
                if (!string.IsNullOrWhiteSpace(template.PagesToRotate))
                {
                    var rotateOrig = parser.Parse(template.PagesToRotate);

                    // Map original page numbers to 1-based indices in the extracted PDF.
                    var indexMap = pageNumbers
                        .Select((p, i) => (Orig: p, Idx: i + 1))
                        .ToDictionary(x => x.Orig, x => x.Idx);

                    var rotateIdx = rotateOrig
                        .Where(p => indexMap.ContainsKey(p))
                        .Select(p => indexMap[p])
                        .ToList();

                    if (rotateIdx.Count > 0)
                    {
                        processed = Path.Combine(workDir, $"rot_{template.Id}.pdf");
                        rotator.Rotate(extracted, rotateIdx, template.RotationDirection, processed);
                    }
                }

                processedPdfs.Add(processed);
            }

            ct.ThrowIfCancellationRequested();

            progress?.Report("Merging PDFs...");
            var merged = Path.Combine(workDir, "merged.pdf");
            merger.Merge(processedPdfs, merged);

            Directory.CreateDirectory(saveLocation);
            var outputPath = Path.Combine(saveLocation, BuildOutputFileName(item));
            // Individual template downloads are not numbered — copy merged PDF directly.
            File.Copy(merged, outputPath, overwrite: true);

            return outputPath;
        }, ct);
    }

    /// <summary>Replaces characters that are invalid in file names with underscores.</summary>
    private static string Sanitize(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { ':', '/', '\\' };
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
