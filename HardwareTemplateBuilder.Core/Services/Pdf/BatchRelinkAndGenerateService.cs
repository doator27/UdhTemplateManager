using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// For each job: relinks its base hardware (remove and recreate <c>JobHardware</c> rows, see
/// <see cref="JobHardwareRelinkService"/>) and then generates its PDF package. Reuses the same
/// collect-and-continue / cancel-aborts semantics and result types as
/// <see cref="BatchJobPackageGenerationService"/>; a job with no hardware to relink is recorded
/// as a (non-fatal) skip rather than being generated.
/// </summary>
public class BatchRelinkAndGenerateService
{
    private readonly Func<AppDbContext> _contextFactory;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new <see cref="BatchRelinkAndGenerateService"/>.
    /// </summary>
    /// <param name="contextFactory">Creates a fresh <see cref="AppDbContext"/> per operation.</param>
    /// <param name="httpClient">Shared HTTP client, built once by the caller and reused across every job.</param>
    public BatchRelinkAndGenerateService(Func<AppDbContext> contextFactory, HttpClient httpClient)
    {
        _contextFactory = contextFactory;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Relinks and generates packages for every job in <paramref name="jobIds"/>, in order.
    /// </summary>
    /// <param name="jobIds">The jobs to process, in the order they should be processed.</param>
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
        var relinkService = new JobHardwareRelinkService();
        var summaries = new List<JobGenerationSummary>();
        int successCount = 0;
        int total = jobIds.Count;

        for (int i = 0; i < jobIds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var jobId = jobIds[i];
            var currentIndex = i + 1;

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

            progress?.Report(new BatchGenerationProgress
            {
                CurrentIndex  = currentIndex,
                TotalJobs     = total,
                JobNumber     = jobNumber,
                StatusMessage = "Relinking hardware..."
            });

            JobGenerationSummary summary;
            try
            {
                RelinkResult relinkResult;
                using (var ctx = _contextFactory())
                {
                    relinkResult = relinkService.Relink(ctx, jobId);
                }

                if (!relinkResult.Success)
                {
                    summary = new JobGenerationSummary
                    {
                        JobId        = jobId,
                        JobNumber    = jobNumber,
                        JobName      = jobName,
                        Success      = false,
                        ErrorMessage = relinkResult.Message
                    };
                    summaries.Add(summary);
                    progress?.Report(new BatchGenerationProgress
                    {
                        CurrentIndex  = currentIndex,
                        TotalJobs     = total,
                        JobNumber     = jobNumber,
                        StatusMessage = "Skipped \u2014 " + relinkResult.Message,
                        CompletedJob  = summary
                    });
                    continue;
                }

                var itemProgress = new Progress<string>(msg => progress?.Report(new BatchGenerationProgress
                {
                    CurrentIndex  = currentIndex,
                    TotalJobs     = total,
                    JobNumber     = jobNumber,
                    StatusMessage = msg
                }));

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
