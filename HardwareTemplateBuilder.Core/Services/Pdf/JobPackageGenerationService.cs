using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>Outcome of a successful <see cref="JobPackageGenerationService.GenerateAsync"/> call.</summary>
public class JobPackageGenerationResult
{
    /// <summary>Gets the ID of the job that was generated.</summary>
    public int JobId { get; init; }

    /// <summary>Gets the job's number, for display in progress/result reporting.</summary>
    public string JobNumber { get; init; } = string.Empty;

    /// <summary>Gets the job's name, for display in progress/result reporting.</summary>
    public string JobName { get; init; } = string.Empty;

    /// <summary>Gets the full path to the generated PDF package.</summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>Gets the number of templates included in the package.</summary>
    public int TemplateCount { get; init; }
}

/// <summary>
/// Generates the PDF template package for a single job: loads job/hardware data, backs up any
/// previous output, pre-downloads templates missing a local copy, runs <see cref="PdfAssemblyService"/>,
/// and records the resulting <see cref="JobTemplateSnapshot"/> rows, frequency increments, and
/// history log entry. This is the UI-agnostic pipeline shared by the single-job "Generate Package"
/// button (<c>JobDetailView</c>) and the batch generation feature (<c>BatchGenerateView</c> via
/// <see cref="BatchJobPackageGenerationService"/>).
/// </summary>
public class JobPackageGenerationService
{
    private readonly Func<AppDbContext> _contextFactory;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new <see cref="JobPackageGenerationService"/>.
    /// </summary>
    /// <param name="contextFactory">
    /// Creates a fresh <see cref="AppDbContext"/> per call — matches the project convention that
    /// contexts are created per-operation, not shared, letting one service instance be reused
    /// safely across many jobs in a batch.
    /// </param>
    /// <param name="httpClient">
    /// Shared HTTP client used for template acquisition/download. Built once by the caller and
    /// reused across every job processed by this service instance.
    /// </param>
    public JobPackageGenerationService(Func<AppDbContext> contextFactory, HttpClient httpClient)
    {
        _contextFactory = contextFactory;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Generates the PDF package for <paramref name="jobId"/>.
    /// </summary>
    /// <param name="jobId">The job to generate a package for.</param>
    /// <param name="orderedHardware">
    /// The job's hardware items in display order, as (HardwareItemId, CustomDescription, CalloutRemarks)
    /// tuples. Pass the caller's live in-session order (e.g. after a drag-and-drop reorder, which is
    /// never persisted) to match what the user currently sees. When <c>null</c>, the canonical DB order
    /// (Manufacturer → Description → ModelNumber) is used — the same order <c>JobDetailView</c> falls
    /// back to on every fresh load.
    /// </param>
    /// <param name="progress">Optional progress reporter; receives per-step status strings.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown with a categorized, human-readable report when one or more hardware items or
    /// templates could not be processed (see <see cref="PdfAssemblyService.AssembleAsync"/>).
    /// </exception>
    public async Task<JobPackageGenerationResult> GenerateAsync(
        int jobId,
        IReadOnlyList<(int HardwareItemId, string? CustomDescription, string? CalloutRemarks)>? orderedHardware = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        int? releaseId = null)
    {
        using var context = _contextFactory();

        var job = context.Jobs
            .Include(j => j.Customer)
            .Include(j => j.ProjectManager)
            .First(j => j.Id == jobId);

        var release = releaseId.HasValue
            ? context.JobReleases.FirstOrDefault(r => r.Id == releaseId.Value)
            : null;
        var displayName = release?.ReleaseLabel ?? job.JobName;

        var orderedItems = orderedHardware ?? LoadCanonicalOrder(context, jobId, releaseId);

        var allItemIds = orderedItems.Select(x => x.HardwareItemId).Distinct().ToList();
        var hardwareDict = context.HardwareItems
            .Include(h => h.Manufacturer)
            .Include(h => h.Description)
            .Where(h => allItemIds.Contains(h.Id))
            .ToDictionary(h => h.Id);

        var hardware = orderedItems
            .Where(x => hardwareDict.ContainsKey(x.HardwareItemId))
            .Select(x =>
            {
                var templates = context.HardwareItemTemplates
                    .Include(hit => hit.IndividualTemplate)
                        .ThenInclude(t => t.Manufacturer)
                    .Include(hit => hit.IndividualTemplate)
                        .ThenInclude(t => t.Description)
                    .Where(hit => hit.HardwareItemId == x.HardwareItemId &&
                                  (hit.JobId == null || hit.JobId == jobId))
                    .Select(hit => hit.IndividualTemplate)
                    .ToList()
                    .AsReadOnly();

                return new HardwareWithTemplates
                {
                    Item              = hardwareDict[x.HardwareItemId],
                    Templates         = templates,
                    CustomDescription = x.CustomDescription,
                    CalloutRemarks    = x.CalloutRemarks
                };
            })
            .ToList()
            .AsReadOnly();

        var profile = context.UserProfiles.FirstOrDefault(u => u.Id == job.UserProfileId);
        var appSettingsDir = new AppSettingRepository(context).GetValue("TemplateStorageLocation");
        if (string.IsNullOrWhiteSpace(appSettingsDir))
            appSettingsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        // Templates always use the shared App Settings location. Job output (this method's
        // concern) may instead go to the user's custom job save location, if one is set.
        var jobLocation = JobStorageLocationResolver.Resolve(appSettingsDir, profile);
        var saveDir = jobLocation.PrimaryRoot;

        var allDescriptions = context.Descriptions.ToDictionary(d => d.Id);

        var request = new AssemblyRequest
        {
            Job             = job,
            Hardware        = hardware,
            OutputDirectory = saveDir,
            AllDescriptions = allDescriptions,
            PreparedByName  = profile?.UserName ?? string.Empty,
            DisplayName     = displayName
        };

        // Back up the entire job folder contents before regenerating.
        // Everything except the "Old versions" folder is moved into
        // Old versions/{timestamp}/ so the previous output is fully preserved.
        var jobDir = Path.Combine(saveDir, job.JobNumber);
        if (Directory.Exists(jobDir))
        {
            var entries = Directory.GetFileSystemEntries(jobDir)
                .Where(e => !Path.GetFileName(e).Equals("Old versions", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(e).Equals("Attachments", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (entries.Count > 0)
            {
                var versionStamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
                var backupDir    = Path.Combine(jobDir, "Old versions", versionStamp);
                Directory.CreateDirectory(backupDir);

                foreach (var entry in entries)
                {
                    var dest = Path.Combine(backupDir, Path.GetFileName(entry));
                    if (File.Exists(entry))
                        File.Move(entry, dest);
                    else if (Directory.Exists(entry))
                        Directory.Move(entry, dest);
                }

                progress?.Report("Backed up previous version...");
            }
        }

        // Pre-download any templates that don't have a valid local file yet.
        // TemplateRefreshService uses the same context, so EF's identity map means
        // the LocalLink update lands on the same template objects already in `hardware`.
        var allUniqueTemplates = hardware
            .SelectMany(h => h.Templates)
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .Where(t => string.IsNullOrWhiteSpace(t.LocalLink) || !File.Exists(t.LocalLink))
            .ToList();

        if (allUniqueTemplates.Count > 0)
        {
            var refreshService = new TemplateRefreshService(context, _httpClient);
            for (int i = 0; i < allUniqueTemplates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var t = allUniqueTemplates[i];
                progress?.Report($"Downloading {t.TemplateNumber} ({i + 1}/{allUniqueTemplates.Count})...");
                try
                {
                    await refreshService.RefreshSingleAsync(t.Id, appSettingsDir, cancellationToken);
                }
                catch
                {
                    // Download failures are reported by AssembleAsync's per-template error collection.
                }
            }
        }

        var assemblyService = BuildAssemblyService(_httpClient);
        var result = await assemblyService.AssembleAsync(request, progress, cancellationToken);

        // Append a history entry to job_history.txt in the job folder.
        var historyPath = Path.Combine(saveDir, job.JobNumber, "job_history.txt");
        AppendHistoryEntry(historyPath, job, displayName, hardware, result, profile?.UserName ?? "Unknown");

        var snapshotRepo = new JobTemplateSnapshotRepository(context);
        foreach (var info in result.TemplateSnapshots)
        {
            snapshotRepo.Add(new JobTemplateSnapshot
            {
                JobId                = jobId,
                IndividualTemplateId = info.IndividualTemplateId,
                SnapshotLocalLink    = info.AcquiredFilePath,
                SnapshotDate         = DateTime.UtcNow,
                PagesToPrint         = info.PagesToPrint,
                PagesToRotate        = info.PagesToRotate,
                RotationDirection    = info.RotationDirection
            });
        }

        var freqService = new FrequencyService(context);
        foreach (var itemId in allItemIds)
        {
            var item = context.HardwareItems.Find(itemId);
            if (item != null) freqService.IncrementFrequency(item);
        }

        // Keep a full copy of the job folder in the shared App Settings location when the user
        // has a distinct custom job save location configured (best-effort; never throws).
        if (jobLocation.SecondaryRoot != null)
        {
            var secondaryJobDir = Path.Combine(jobLocation.SecondaryRoot, job.JobNumber);
            DirectoryMirrorHelper.CopyDirectoryContents(jobDir, secondaryJobDir);
        }

        // Note: master/local database sync is now triggered automatically by every
        // AppDbContext.SaveChanges call (see DatabaseSyncService), so no explicit sync is
        // needed here.

        return new JobPackageGenerationResult
        {
            JobId         = jobId,
            JobNumber     = job.JobNumber,
            JobName       = displayName,
            OutputPath    = result.OutputPath,
            TemplateCount = result.TemplateSnapshots.Count
        };
    }

    /// <summary>
    /// Loads a job's hardware in the same canonical order <c>JobDetailView.LoadLinkedHardware</c>
    /// falls back to on every fresh load: Manufacturer → Description → ModelNumber.
    /// </summary>
    private static List<(int HardwareItemId, string? CustomDescription, string? CalloutRemarks)> LoadCanonicalOrder(
        AppDbContext context, int jobId, int? releaseId = null) =>
        context.JobHardware
            .Include(jh => jh.HardwareItem)
                .ThenInclude(h => h.Manufacturer)
            .Include(jh => jh.HardwareItem)
                .ThenInclude(h => h.Description)
            .Where(jh => jh.JobId == jobId && jh.ReleaseId == releaseId)
            .OrderBy(jh => jh.HardwareItem!.Manufacturer!.ManufacturerName)
            .ThenBy(jh => jh.HardwareItem!.Description!.DescriptionText)
            .ThenBy(jh => jh.HardwareItem!.ModelNumber)
            .Select(jh => new ValueTuple<int, string?, string?>(
                jh.HardwareItemId,
                jh.CustomDescription,
                !string.IsNullOrWhiteSpace(jh.CalloutRemarks) ? jh.CalloutRemarks : jh.Remarks))
            .ToList();

    /// <summary>Creates a fully wired <see cref="PdfAssemblyService"/> using the supplied HTTP client.</summary>
    private static PdfAssemblyService BuildAssemblyService(HttpClient httpClient) =>
        new PdfAssemblyService(
            new TemplateSorter(new WeightTemplateSortStrategy()),
            new FileAcquirer(httpClient),
            new PageRangeParser(),
            new PageExtractor(),
            new PageRotator(),
            new PdfMerger(),
            new CoverSheetBuilder(),
            new PageNumberer());

    /// <summary>
    /// Appends a single generation record to <paramref name="historyPath"/>, creating the file
    /// if it does not yet exist. Each record captures the timestamp, user, hardware list,
    /// and output file name so the full history of a job's packages is preserved in plain text.
    /// </summary>
    private static void AppendHistoryEntry(
        string historyPath,
        Job job,
        string displayName,
        IReadOnlyList<HardwareWithTemplates> hardware,
        AssemblyResult result,
        string preparedBy)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine($"Job:         {job.JobNumber} — {displayName}");
            sb.AppendLine($"Prepared by: {preparedBy}");
            sb.AppendLine($"Hardware Items ({hardware.Count}):");

            int n = 1;
            foreach (var hwt in hardware)
            {
                var item  = hwt.Item;
                var mfr   = item.Manufacturer?.ManufacturerName ?? "Unknown";
                var desc  = item.Description?.DescriptionText   ?? string.Empty;
                var label = !string.IsNullOrWhiteSpace(hwt.CustomDescription)
                    ? hwt.CustomDescription!
                    : item.ModelNumber;
                var tplNums = string.Join(", ", hwt.Templates.Select(t => t.TemplateNumber));

                sb.AppendLine($"  {n++,2}. {mfr} — {label}" +
                              (string.IsNullOrEmpty(desc) ? string.Empty : $" [{desc}]"));
                if (!string.IsNullOrEmpty(tplNums))
                    sb.AppendLine($"       Templates: {tplNums}");
            }

            sb.AppendLine($"Output: {Path.GetFileName(result.OutputPath)}");
            sb.AppendLine(new string('─', 60));
            sb.AppendLine();

            File.AppendAllText(historyPath, sb.ToString());
        }
        catch
        {
            // History write failure is non-fatal.
        }
    }
}
