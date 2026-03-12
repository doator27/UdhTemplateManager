using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>
/// Repository for <see cref="JobTemplateSnapshot"/> entities.
/// Snapshots are immutable — no duplicate check is needed since each generation creates a new snapshot.
/// </summary>
public class JobTemplateSnapshotRepository : RepositoryBase<JobTemplateSnapshot>
{
    /// <inheritdoc/>
    public JobTemplateSnapshotRepository(AppDbContext context) : base(context) { }

    // No FindDuplicate override — snapshots are always new records (immutable, append-only)
}
