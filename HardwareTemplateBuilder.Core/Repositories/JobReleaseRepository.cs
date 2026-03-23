using System.Collections.Generic;
using System.Linq;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>Repository for <see cref="JobRelease"/> entities.</summary>
public class JobReleaseRepository : RepositoryBase<JobRelease>
{
    /// <inheritdoc/>
    public JobReleaseRepository(AppDbContext context) : base(context) { }

    /// <summary>
    /// Returns all releases for the specified job, ordered by <see cref="JobRelease.ReleaseNumber"/>.
    /// </summary>
    public IEnumerable<JobRelease> GetByJob(int jobId) =>
        _context.JobReleases
            .Where(r => r.JobId == jobId)
            .OrderBy(r => r.ReleaseNumber)
            .ToList();

    /// <summary>
    /// Returns the next available release number for the specified job
    /// (max existing + 1, or 1 if none exist).
    /// </summary>
    public int NextReleaseNumber(int jobId)
    {
        var max = _context.JobReleases
            .Where(r => r.JobId == jobId)
            .Select(r => (int?)r.ReleaseNumber)
            .Max();
        return (max ?? 0) + 1;
    }

    /// <inheritdoc/>
    /// <remarks>Release labels must be unique within a job.</remarks>
    protected override JobRelease? FindDuplicate(JobRelease entity) =>
        _context.JobReleases.FirstOrDefault(r =>
            r.JobId        == entity.JobId &&
            r.ReleaseLabel == entity.ReleaseLabel);
}
