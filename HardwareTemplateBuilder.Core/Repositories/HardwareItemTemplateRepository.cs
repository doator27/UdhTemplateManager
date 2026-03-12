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
            hit.HardwareItemId == entity.HardwareItemId &&
            hit.IndividualTemplateId == entity.IndividualTemplateId);
}
