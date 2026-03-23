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

    /// <summary>Returns the draft for the given job, or null if none exists.</summary>
    public BulkAddDraft? GetByJob(int jobId) =>
        _context.BulkAddDrafts.FirstOrDefault(d => d.JobId == jobId);

    /// <summary>
    /// Saves or replaces the draft JSON for the given job.
    /// Existing draft is overwritten; a new one is created if none exists.
    /// </summary>
    public void Upsert(int jobId, string json)
    {
        var existing = GetByJob(jobId);
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
                DraftJson = json,
                SavedAt  = DateTime.UtcNow
            });
            _context.SaveChanges();
        }
    }

    /// <summary>Deletes any saved draft for the given job.</summary>
    public void DeleteByJob(int jobId)
    {
        var existing = GetByJob(jobId);
        if (existing != null)
        {
            _context.BulkAddDrafts.Remove(existing);
            _context.SaveChanges();
        }
    }

    /// <inheritdoc/>
    protected override BulkAddDraft? FindDuplicate(BulkAddDraft entity) =>
        GetByJob(entity.JobId);
}
