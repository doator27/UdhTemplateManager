using System.Collections.Generic;
using System.Linq;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>
/// Repository for <see cref="JobAttachment"/> records.
/// Each attachment is unique by stored path, so duplicates are detected on
/// <see cref="StoredPath"/> to prevent the same copied file from being inserted twice.
/// </summary>
public class JobAttachmentRepository : RepositoryBase<JobAttachment>
{
    /// <inheritdoc/>
    public JobAttachmentRepository(AppDbContext context) : base(context) { }

    /// <summary>Returns all attachments for the given job, ordered by date added.</summary>
    public IEnumerable<JobAttachment> GetByJob(int jobId) =>
        _context.JobAttachments
                .Where(a => a.JobId == jobId)
                .OrderBy(a => a.DateAdded)
                .ToList();

    /// <inheritdoc/>
    protected override JobAttachment? FindDuplicate(JobAttachment entity) =>
        _context.JobAttachments.FirstOrDefault(a => a.StoredPath == entity.StoredPath);
}
