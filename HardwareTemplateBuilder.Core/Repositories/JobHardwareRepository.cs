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
    /// <remarks>
    /// Returns null — the same hardware item may appear multiple times on a job,
    /// each with an independent <c>CustomDescription</c>.
    /// </remarks>
    protected override JobHardware? FindDuplicate(JobHardware entity) => null;
}
