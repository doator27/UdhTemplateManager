using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>Repository for <see cref="Job"/> entities.</summary>
public class JobRepository : RepositoryBase<Job>
{
    /// <inheritdoc/>
    public JobRepository(AppDbContext context) : base(context) { }

    /// <inheritdoc/>
    protected override Job? FindDuplicate(Job entity) =>
        _context.Jobs.FirstOrDefault(j => j.JobNumber == entity.JobNumber);
}
