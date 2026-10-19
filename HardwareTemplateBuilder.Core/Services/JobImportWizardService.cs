using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Step-through import of Jobs and their associated Hardware (<see cref="JobHardware"/>) from a
/// legacy SQLite database into the master database. Unlike <see cref="DatabaseMergeImportService"/>
/// (which merges every table at once and requires a single all-or-nothing confirmation), this
/// service only considers Jobs and the records a job directly needs (Customer, ProjectManager,
/// Manufacturer, Description, HardwareItem), and is designed to be driven one job at a time by a
/// wizard UI so the user can accept or skip each job individually.
/// <para>
/// Jobs are matched between databases by identity (JobNumber + JobName + Customer name + Project
/// Manager name + IsComplete + Notes, ignoring timestamps) — identical jobs are never shown.
/// Jobs that exist already but are missing some hardware lines are shown with only the missing
/// lines. Hardware lines are matched to existing HardwareItems by Manufacturer + Description path
/// + Model Number; anything not found is created when the job is accepted.
/// </para>
/// </summary>
public sealed class JobImportWizardService
{
    private readonly Func<AppDbContext> _targetContextFactory;

    /// <summary>Initializes a new instance of <see cref="JobImportWizardService"/>.</summary>
    /// <param name="targetContextFactory">Creates a new <see cref="AppDbContext"/> pointed at the master database.</param>
    public JobImportWizardService(Func<AppDbContext> targetContextFactory)
    {
        _targetContextFactory = targetContextFactory;
    }

    /// <summary>
    /// Reads <paramref name="sqliteFilePath"/> and the master database and computes the list of
    /// jobs that need review (missing entirely, or missing some hardware lines). Does not write
    /// anything. Call <see cref="ApplyJobAsync"/> per accepted candidate afterwards.
    /// </summary>
    public async Task<List<UserProfile>> GetSourceUsersAsync(string sqliteFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(sqliteFilePath))
        {
            throw new FileNotFoundException("Legacy SQLite database file not found.", sqliteFilePath);
        }

        var sourceOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={sqliteFilePath};Mode=ReadOnly")
            .Options;

        await using var source = new AppDbContext(sourceOptions);
        return await source.Set<UserProfile>().AsNoTracking().OrderBy(u => u.UserName).ToListAsync(ct);
    }

    public async Task<JobImportWizardPlan> PreviewAsync(string sqliteFilePath, int? sourceUserProfileId = null, CancellationToken ct = default)
    {
        if (!File.Exists(sqliteFilePath))
        {
            throw new FileNotFoundException("Legacy SQLite database file not found.", sqliteFilePath);
        }

        var sourceOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={sqliteFilePath};Mode=ReadOnly")
            .Options;

        await using var source = new AppDbContext(sourceOptions);
        await using var target = _targetContextFactory();

        var plan = new JobImportWizardPlan { SourceFilePath = sqliteFilePath };

        // ── Load everything needed, from both databases ────────────────────────────
        var sourceJobsQuery = source.Set<Job>().AsNoTracking().AsQueryable();
        if (sourceUserProfileId.HasValue)
        {
            sourceJobsQuery = sourceJobsQuery.Where(j => j.UserProfileId == sourceUserProfileId.Value);
        }
        var sourceJobs = await sourceJobsQuery.ToListAsync(ct);
        var sourceUserProfiles = await source.Set<UserProfile>().AsNoTracking().ToDictionaryAsync(u => u.Id, ct);
        var sourceCustomers = await source.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var sourceProjectManagers = await source.Set<ProjectManager>().AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
        var sourceJobHardware = await source.Set<JobHardware>().AsNoTracking().Where(h => h.ReleaseId == null).ToListAsync(ct);
        var sourceHardwareItems = await source.Set<HardwareItem>().AsNoTracking().ToDictionaryAsync(h => h.Id, ct);
        var sourceManufacturers = await source.Set<Manufacturer>().AsNoTracking().ToDictionaryAsync(m => m.Id, ct);
        var sourceDescriptions = await source.Set<Description>().AsNoTracking().ToListAsync(ct);

        var targetJobs = await target.Set<Job>().AsNoTracking().ToListAsync(ct);
        var targetCustomers = await target.Set<Customer>().AsNoTracking().ToListAsync(ct);
        var targetProjectManagers = await target.Set<ProjectManager>().AsNoTracking().ToListAsync(ct);
        var targetJobHardware = await target.Set<JobHardware>().AsNoTracking().Where(h => h.ReleaseId == null).ToListAsync(ct);
        var targetHardwareItems = await target.Set<HardwareItem>().AsNoTracking().ToListAsync(ct);
        var targetManufacturers = await target.Set<Manufacturer>().AsNoTracking().ToDictionaryAsync(m => m.Id, ct);
        var targetDescriptions = await target.Set<Description>().AsNoTracking().ToListAsync(ct);

        // Normalize so the same record authored with different casing/whitespace in the two
        // databases (a common source of false "missing"/"duplicate" results) still matches,
        // mirroring the case-insensitive identity comparisons used by DatabaseMergeImportService.
        static string Norm(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

        string HwKey(int manufacturerId, int? descriptionId, string modelNumber, IReadOnlyDictionary<int, Manufacturer> mfrs, IReadOnlyList<Description> descs) =>
            $"{Norm(mfrs.TryGetValue(manufacturerId, out var m) ? m.ManufacturerName : "?")}|{Norm(DescriptionPathService.GetFolderPath(descriptionId, descs))}|{Norm(modelNumber)}";

        var targetHardwareByKey = targetHardwareItems
            .GroupBy(h => HwKey(h.ManufacturerId, h.DescriptionId, h.ModelNumber, targetManufacturers, targetDescriptions))
            .ToDictionary(g => g.Key, g => g.First().Id);

        var targetCustomerNames = new HashSet<string>(targetCustomers.Select(c => c.CustomerName), StringComparer.OrdinalIgnoreCase);
        var targetPmNames = new HashSet<string>(targetProjectManagers.Select(p => p.ProjectManagerName), StringComparer.OrdinalIgnoreCase);

        string JobKey(string jobNumber, string jobName, string customerName, string pmName, bool isComplete, string? notes) =>
            $"{Norm(jobNumber)}|{Norm(jobName)}|{Norm(customerName)}|{Norm(pmName)}|{isComplete}|{Norm(notes)}";

        var targetJobsByKey = new Dictionary<string, Job>();
        foreach (var tj in targetJobs)
        {
            var custName = targetCustomers.FirstOrDefault(c => c.Id == tj.CustomerId)?.CustomerName ?? "?";
            var pmName = targetProjectManagers.FirstOrDefault(p => p.Id == tj.ProjectManagerId)?.ProjectManagerName ?? "?";
            targetJobsByKey[JobKey(tj.JobNumber, tj.JobName, custName, pmName, tj.IsComplete, tj.Notes)] = tj;
        }

        var targetJobHardwareByJobId = targetJobHardware
            .GroupBy(h => h.JobId)
            .ToDictionary(g => g.Key, g => g.Select(h => h.HardwareItemId).ToHashSet());

        var sourceJobHardwareByJobId = sourceJobHardware
            .GroupBy(h => h.JobId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var upToDateCount = 0;

        foreach (var job in sourceJobs)
        {
            var customerName = sourceCustomers.TryGetValue(job.CustomerId, out var cust) ? cust.CustomerName : "?";
            var pmName = sourceProjectManagers.TryGetValue(job.ProjectManagerId, out var pm) ? pm.ProjectManagerName : "?";
            var createdByUserName = sourceUserProfiles.TryGetValue(job.UserProfileId, out var up) ? up.UserName : "?";
            var key = JobKey(job.JobNumber, job.JobName, customerName, pmName, job.IsComplete, job.Notes);

            sourceJobHardwareByJobId.TryGetValue(job.Id, out var hwLines);
            hwLines ??= new List<JobHardware>();

            var matchedJob = targetJobsByKey.TryGetValue(key, out var existingJob) ? existingJob : null;

            List<HardwareLineCandidate> missingLines;
            JobCandidateStatus status;
            int? existingJobId = null;

            if (matchedJob != null)
            {
                existingJobId = matchedJob.Id;
                targetJobHardwareByJobId.TryGetValue(matchedJob.Id, out var existingHwItemIds);
                existingHwItemIds ??= new HashSet<int>();

                missingLines = new List<HardwareLineCandidate>();
                foreach (var line in hwLines)
                {
                    if (!sourceHardwareItems.TryGetValue(line.HardwareItemId, out var srcHw))
                        continue;

                    var hwKey = HwKey(srcHw.ManufacturerId, srcHw.DescriptionId, srcHw.ModelNumber, sourceManufacturers, sourceDescriptions);
                    var isNewHw = !targetHardwareByKey.TryGetValue(hwKey, out var matchedHwId);

                    // Already linked on the existing job? (only meaningful when hw already matched)
                    if (!isNewHw && existingHwItemIds.Contains(matchedHwId))
                        continue;

                    missingLines.Add(BuildHardwareLineCandidate(srcHw, sourceManufacturers, sourceDescriptions, targetHardwareByKey, line));
                }

                if (missingLines.Count == 0)
                {
                    upToDateCount++;
                    continue;
                }

                status = JobCandidateStatus.MissingHardwareOnly;
            }
            else
            {
                missingLines = new List<HardwareLineCandidate>();
                foreach (var line in hwLines)
                {
                    if (!sourceHardwareItems.TryGetValue(line.HardwareItemId, out var srcHw))
                        continue;

                    missingLines.Add(BuildHardwareLineCandidate(srcHw, sourceManufacturers, sourceDescriptions, targetHardwareByKey, line));
                }

                status = JobCandidateStatus.Missing;
            }

            plan.Candidates.Add(new JobImportCandidate
            {
                SourceJobId = job.Id,
                JobNumber = job.JobNumber,
                JobName = job.JobName,
                CustomerName = customerName,
                CustomerIsNew = !targetCustomerNames.Contains(customerName),
                ProjectManagerName = pmName,
                ProjectManagerIsNew = !targetPmNames.Contains(pmName),
                CreatedByUserName = createdByUserName,
                IsComplete = job.IsComplete,
                Notes = job.Notes,
                Status = status,
                ExistingJobId = existingJobId,
                HardwareLines = missingLines
            });
        }

        return new JobImportWizardPlan
        {
            SourceFilePath = sqliteFilePath,
            Candidates = plan.Candidates,
            AlreadyUpToDateCount = upToDateCount
        };
    }

    private static HardwareLineCandidate BuildHardwareLineCandidate(
        HardwareItem srcHw,
        IReadOnlyDictionary<int, Manufacturer> sourceManufacturers,
        IReadOnlyList<Description> sourceDescriptions,
        IReadOnlyDictionary<string, int> targetHardwareByKey,
        JobHardware line)
    {
        var mfrName = sourceManufacturers.TryGetValue(srcHw.ManufacturerId, out var mfr) ? mfr.ManufacturerName : "?";
        var descPath = DescriptionPathService.GetFolderPath(srcHw.DescriptionId, sourceDescriptions);
        var hwKey = $"{mfrName}|{descPath}|{srcHw.ModelNumber}";
        var hwExists = targetHardwareByKey.ContainsKey(hwKey);

        return new HardwareLineCandidate
        {
            ManufacturerName = mfrName,
            DescriptionPath = descPath,
            ModelNumber = srcHw.ModelNumber,
            CustomDescription = line.CustomDescription,
            Remarks = line.Remarks,
            CalloutRemarks = line.CalloutRemarks,
            ManufacturerIsNew = false, // resolved precisely at apply time; preview treats missing hw as possibly needing a new manufacturer/description too
            DescriptionIsNew = false,
            HardwareItemIsNew = !hwExists
        };
    }

    /// <summary>
    /// Applies a single accepted <see cref="JobImportCandidate"/> to the master database:
    /// creates any missing Customer/ProjectManager/Manufacturer/Description/HardwareItem records
    /// needed, inserts the Job if it doesn't already exist (using the next available Id if its
    /// original Id is taken by an unrelated record), and inserts the missing JobHardware links.
    /// Runs inside a single retriable transaction.
    /// </summary>
    public async Task<JobImportApplyResult> ApplyJobAsync(JobImportCandidate candidate, CancellationToken ct = default)
    {
        await using var target = _targetContextFactory();
        var strategy = target.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            target.ChangeTracker.Clear();
            await using var transaction = await target.Database.BeginTransactionAsync(ct);
            try
            {
                var result = new JobImportApplyResult();

                // ── Resolve/create Customer + ProjectManager ───────────────────────
                var customer = await target.Set<Customer>()
                    .FirstOrDefaultAsync(c => c.CustomerName.Trim().ToLower() == candidate.CustomerName.Trim().ToLower(), ct);
                if (customer == null)
                {
                    customer = new Customer { CustomerName = candidate.CustomerName };
                    target.Set<Customer>().Add(customer);
                    await target.SaveChangesAsync(ct);
                }

                var projectManager = await target.Set<ProjectManager>()
                    .FirstOrDefaultAsync(p => p.ProjectManagerName.Trim().ToLower() == candidate.ProjectManagerName.Trim().ToLower(), ct);
                if (projectManager == null)
                {
                    projectManager = new ProjectManager { ProjectManagerName = candidate.ProjectManagerName };
                    target.Set<ProjectManager>().Add(projectManager);
                    await target.SaveChangesAsync(ct);
                }

                // ── Resolve/create the Job ─────────────────────────────────────────
                int jobId;
                if (candidate.Status == JobCandidateStatus.MissingHardwareOnly && candidate.ExistingJobId.HasValue)
                {
                    jobId = candidate.ExistingJobId.Value;
                }
                else
                {
                    // Need a UserProfileId — reuse the first available profile to satisfy the FK;
                    // the user can reassign creator in the UI afterward if desired.
                    var anyUserProfileId = await target.Set<UserProfile>().Select(u => u.Id).FirstOrDefaultAsync(ct);

                    var newJob = new Job
                    {
                        JobNumber = candidate.JobNumber,
                        JobName = candidate.JobName,
                        CustomerId = customer.Id,
                        ProjectManagerId = projectManager.Id,
                        UserProfileId = anyUserProfileId,
                        IsComplete = candidate.IsComplete,
                        Notes = candidate.Notes
                    };
                    target.Set<Job>().Add(newJob);
                    await target.SaveChangesAsync(ct);
                    jobId = newJob.Id;
                }

                // ── Resolve/create Manufacturer, Description, HardwareItem for each line ──
                foreach (var line in candidate.HardwareLines)
                {
                    var manufacturer = await target.Set<Manufacturer>()
                        .FirstOrDefaultAsync(m => m.ManufacturerName.Trim().ToLower() == line.ManufacturerName.Trim().ToLower(), ct);
                    if (manufacturer == null)
                    {
                        manufacturer = new Manufacturer { ManufacturerName = line.ManufacturerName };
                        target.Set<Manufacturer>().Add(manufacturer);
                        await target.SaveChangesAsync(ct);
                        result.ManufacturersCreated++;
                    }

                    int? descriptionId = null;
                    if (!string.IsNullOrWhiteSpace(line.DescriptionPath))
                    {
                        descriptionId = await ResolveOrCreateDescriptionPathAsync(target, line.DescriptionPath, ct, result);
                    }

                    var hardwareItem = await target.Set<HardwareItem>()
                        .FirstOrDefaultAsync(h =>
                            h.ManufacturerId == manufacturer.Id &&
                            h.DescriptionId == descriptionId &&
                            h.ModelNumber == line.ModelNumber, ct);

                    if (hardwareItem == null)
                    {
                        hardwareItem = new HardwareItem
                        {
                            ManufacturerId = manufacturer.Id,
                            DescriptionId = descriptionId ?? 0,
                            ModelNumber = line.ModelNumber,
                            IsActive = true
                        };
                        target.Set<HardwareItem>().Add(hardwareItem);
                        await target.SaveChangesAsync(ct);
                        result.HardwareItemsCreated++;
                    }

                    var alreadyLinked = await target.Set<JobHardware>().AnyAsync(jh =>
                        jh.JobId == jobId && jh.HardwareItemId == hardwareItem.Id && jh.ReleaseId == null, ct);
                    if (alreadyLinked)
                        continue;

                    target.Set<JobHardware>().Add(new JobHardware
                    {
                        JobId = jobId,
                        HardwareItemId = hardwareItem.Id,
                        CustomDescription = line.CustomDescription,
                        Remarks = line.Remarks,
                        CalloutRemarks = line.CalloutRemarks
                    });
                    result.HardwareLinksAdded++;
                }

                await target.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return new JobImportApplyResult
                {
                    JobId = jobId,
                    HardwareLinksAdded = result.HardwareLinksAdded,
                    HardwareItemsCreated = result.HardwareItemsCreated,
                    ManufacturersCreated = result.ManufacturersCreated,
                    DescriptionsCreated = result.DescriptionsCreated
                };
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        });
    }

    /// <summary>
    /// Resolves (or creates) the chain of <see cref="Description"/> nodes represented by
    /// <paramref name="path"/> (e.g. "Hinges\Butt Hinges"), returning the leaf node's Id.
    /// </summary>
    private static async Task<int> ResolveOrCreateDescriptionPathAsync(
        AppDbContext target, string path, CancellationToken ct, JobImportApplyResult result)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int? parentId = null;
        Description? node = null;

        foreach (var segment in segments)
        {
            node = await target.Set<Description>()
                .FirstOrDefaultAsync(d => d.DescriptionText == segment && d.ParentId == parentId, ct);

            if (node == null)
            {
                node = new Description { DescriptionText = segment, ParentId = parentId };
                target.Set<Description>().Add(node);
                await target.SaveChangesAsync(ct);
                result.DescriptionsCreated++;
            }

            parentId = node.Id;
        }

        return node!.Id;
    }
}
