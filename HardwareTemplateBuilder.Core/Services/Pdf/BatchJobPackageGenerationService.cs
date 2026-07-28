using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>Result of generating a single job's package as part of a batch run.</summary>
public class JobGenerationSummary
{
    /// <summary>Gets the ID of the job this summary describes.</summary>
    public int JobId { get; init; }

    /// <summary>Gets the job's number.</summary>
    public string JobNumber { get; init; } = string.Empty;

    /// <summary>Gets the job's name.</summary>
    public string JobName { get; init; } = string.Empty;

    /// <summary>Gets whether generation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the generated PDF's path, when <see cref="Success"/> is <c>true</c>.</summary>
    public string? OutputPath { get; init; }

    /// <summary>
    /// Gets the categorized failure report (see <c>PdfAssemblyService.AssembleAsync</c>) or a
    /// plain error message, when <see cref="Success"/> is <c>false</c>.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>Live progress for a batch run: which job is active and what step it's on.</summary>
public class BatchGenerationProgress
{
    /// <summary>Gets the 1-based position of the current job within the batch.</summary>
    public int CurrentIndex { get; init; }

    /// <summary>Gets the total number of jobs in the batch.</summary>
    public int TotalJobs { get; init; }

    /// <summary>Gets the current job's number, for display.</summary>
    public string JobNumber { get; init; } = string.Empty;

    /// <summary>Gets a short status string describing the current step (e.g. "Acquiring TPL-102...").</summary>
    public string StatusMessage { get; init; } = string.Empty;

    /// <summary>
    /// Gets the just-finished job's summary. Non-null exactly once per job, on the progress
    /// report that marks its completion — this is what a caller should use to build a running
    /// results list, rather than trying to infer completion from <see cref="StatusMessage"/>.
    /// </summary>
    public JobGenerationSummary? CompletedJob { get; init; }
}

/// <summary>Final outcome of a batch run.</summary>
public class BatchGenerationResult
{
    /// <summary>Gets the total number of jobs attempted.</summary>
    public int TotalJobs { get; init; }

    /// <summary>Gets how many of them succeeded.</summary>
    public int SuccessCount { get; init; }

    /// <summary>Gets every job's outcome, in the order they were processed.</summary>
    public IReadOnlyList<JobGenerationSummary> Jobs { get; init; } = Array.Empty<JobGenerationSummary>();
}

/// <summary>
/// Generates PDF packages for many jobs in one run. Each job is independent: a failure is
/// recorded and the batch moves on to the next job, so one broken job cannot block the rest.
/// A cancellation request, in contrast, aborts the whole run — it is a deliberate stop, not a
/// per-job skip. Modeled on the same collect-and-continue / cancel-aborts pattern used by
/// <see cref="TemplateRefreshService.RefreshAsync"/>.
/// </summary>
public class BatchJobPackageGenerationService
{
    private readonly Func<AppDbContext> _contextFactory;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new <see cref="BatchJobPackageGenerationService"/>.
    /// </summary>
    /// <param name="contextFactory">Creates a fresh <see cref="AppDbContext"/> per operation.</param>
    /// <param name="httpClient">Shared HTTP client, built once by the caller and reused across every job.</param>
    public BatchJobPackageGenerationService(Func<AppDbContext> contextFactory, HttpClient httpClient)
    {
        _contextFactory = contextFactory;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Generates packages for every job in <paramref name="jobIds"/>, in order.
    /// </summary>
    /// <param name="jobIds">The jobs to generate, in the order they should be processed.</param>
    /// <param name="progress">Optional progress reporter; see <see cref="BatchGenerationProgress"/>.</param>
    /// <param name="cancellationToken">
    /// Token to cancel the whole batch. Unlike a per-job failure, cancellation stops the run
    /// immediately rather than skipping to the next job.
    /// </param>
    public async Task<BatchGenerationResult> RunBatchAsync(
        IReadOnlyList<int> jobIds,
        IProgress<BatchGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var generationService = new JobPackageGenerationService(_contextFactory, _httpClient);
        var summaries = new List<JobGenerationSummary>();
        int successCount = 0;
        int total = jobIds.Count;

        for (int i = 0; i < jobIds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var jobId = jobIds[i];
            var currentIndex = i + 1;

            // Look up the job's identity up front so progress/failure reporting always has a
            // job number to show, even if GenerateAsync fails before it gets that far itself
            // (e.g. the job was deleted between selection and this batch run).
            string jobNumber;
            string jobName;
            using (var ctx = _contextFactory())
            {
                var job = ctx.Jobs.Find(jobId);
                if (job == null)
                {
                    var missingSummary = new JobGenerationSummary
                    {
                        JobId        = jobId,
                        JobNumber    = $"#{jobId}",
                        JobName      = string.Empty,
                        Success      = false,
                        ErrorMessage = "Job no longer exists."
                    };
                    summaries.Add(missingSummary);
                    progress?.Report(new BatchGenerationProgress
                    {
                        CurrentIndex  = currentIndex,
                        TotalJobs     = total,
                        JobNumber     = missingSummary.JobNumber,
                        StatusMessage = "Job not found.",
                        CompletedJob  = missingSummary
                    });
                    continue;
                }
                jobNumber = job.JobNumber;
                jobName   = job.JobName;
            }

            var itemProgress = new Progress<string>(msg => progress?.Report(new BatchGenerationProgress
            {
                CurrentIndex  = currentIndex,
                TotalJobs     = total,
                JobNumber     = jobNumber,
                StatusMessage = msg
            }));

            JobGenerationSummary summary;
            try
            {
                var result = await generationService.GenerateAsync(jobId, null, itemProgress, cancellationToken);
                summary = new JobGenerationSummary
                {
                    JobId      = jobId,
                    JobNumber  = result.JobNumber,
                    JobName    = result.JobName,
                    Success    = true,
                    OutputPath = result.OutputPath
                };
                successCount++;
            }
            catch (OperationCanceledException)
            {
                // Propagate cancellation so the caller knows the batch was cut short.
                throw;
            }
            catch (Exception ex)
            {
                summary = new JobGenerationSummary
                {
                    JobId        = jobId,
                    JobNumber    = jobNumber,
                    JobName      = jobName,
                    Success      = false,
                    ErrorMessage = ex.Message
                };
            }

            summaries.Add(summary);
            progress?.Report(new BatchGenerationProgress
            {
                CurrentIndex  = currentIndex,
                TotalJobs     = total,
                JobNumber     = summary.JobNumber,
                StatusMessage = summary.Success ? "Done." : "Failed.",
                CompletedJob  = summary
            });
        }

        return new BatchGenerationResult
        {
            TotalJobs    = total,
            SuccessCount = successCount,
            Jobs         = summaries.AsReadOnly()
        };
    }
}
