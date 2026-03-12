using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>Repository for <see cref="JobHardware"/> junction entities.</summary>
public class JobHardwareRepository : RepositoryBase<JobHardware>
{
    /// <inheritdoc/>
    public JobHardwareRepository(AppDbContext context) : base(context) { }

    /// <inheritdoc/>
    protected override JobHardware? FindDuplicate(JobHardware entity) =>
        _context.JobHardware.FirstOrDefault(jh =>
            jh.JobId == entity.JobId &&
            jh.HardwareItemId == entity.HardwareItemId);
}
