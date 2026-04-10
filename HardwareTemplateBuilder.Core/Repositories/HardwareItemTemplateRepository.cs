using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>Repository for <see cref="HardwareItemTemplate"/> junction entities.</summary>
public class HardwareItemTemplateRepository : RepositoryBase<HardwareItemTemplate>
{
    /// <inheritdoc/>
    public HardwareItemTemplateRepository(AppDbContext context) : base(context) { }

    /// <inheritdoc/>
    protected override HardwareItemTemplate? FindDuplicate(HardwareItemTemplate entity) =>
        _context.HardwareItemTemplates.FirstOrDefault(hit =>
            hit.HardwareItemId       == entity.HardwareItemId       &&
            hit.IndividualTemplateId == entity.IndividualTemplateId &&
            hit.JobId                == entity.JobId);

    /// <summary>
    /// Returns the <see cref="HardwareItemTemplate"/> records for a hardware item that are
    /// visible for the given job: global links (<c>JobId IS NULL</c>) plus links scoped to
    /// <paramref name="jobId"/>.
    /// </summary>
    /// <param name="hardwareItemId">The hardware item to query links for.</param>
    /// <param name="jobId">The job context; pass null to return only global links.</param>
    public IQueryable<HardwareItemTemplate> GetByHardwareItemForJob(int hardwareItemId, int? jobId) =>
        _context.HardwareItemTemplates
            .Where(hit => hit.HardwareItemId == hardwareItemId &&
                          (hit.JobId == null || hit.JobId == jobId));
}
