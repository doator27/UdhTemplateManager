using System;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>Repository for <see cref="BulkAddDraft"/> entities.</summary>
public class BulkAddDraftRepository : RepositoryBase<BulkAddDraft>
{
    /// <inheritdoc/>
    public BulkAddDraftRepository(AppDbContext context) : base(context) { }

    /// <summary>
    /// Returns the draft for the given job and release (null = the base list), or null if none exists.
    /// Each release has its own bulk add.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="releaseId">The release (null = base list).</param>
    public BulkAddDraft? GetByJob(int jobId, int? releaseId = null) =>
        _context.BulkAddDrafts
            .Where(d => d.JobId == jobId && d.ReleaseId == releaseId)
            .OrderByDescending(d => d.SavedAt)
            .FirstOrDefault();

    /// <summary>
    /// Saves or replaces the draft JSON for the given job and release (null = base list).
    /// Existing draft is overwritten; a new one is created if none exists.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="json">The draft JSON.</param>
    /// <param name="releaseId">The release (null = base list).</param>
    public void Upsert(int jobId, string json, int? releaseId = null)
    {
        var existing = GetByJob(jobId, releaseId);
        if (existing != null)
        {
            existing.DraftJson = json;
            existing.SavedAt   = DateTime.UtcNow;
            _context.SaveChanges();
        }
        else
        {
            _context.BulkAddDrafts.Add(new BulkAddDraft
            {
                JobId    = jobId,
                ReleaseId = releaseId,
                DraftJson = json,
                SavedAt  = DateTime.UtcNow
            });
            _context.SaveChanges();
        }
    }

    /// <summary>Deletes any saved draft for the given job and release (null = base list).</summary>
    /// <param name="jobId">The job.</param>
    /// <param name="releaseId">The release (null = base list).</param>
    public void DeleteByJob(int jobId, int? releaseId = null)
    {
        var existing = _context.BulkAddDrafts.Where(d => d.JobId == jobId && d.ReleaseId == releaseId).ToList();
        if (existing.Count > 0)
        {
            _context.BulkAddDrafts.RemoveRange(existing);
            _context.SaveChanges();
        }
    }

    /// <inheritdoc/>
    protected override BulkAddDraft? FindDuplicate(BulkAddDraft entity) =>
        GetByJob(entity.JobId, entity.ReleaseId);
}
