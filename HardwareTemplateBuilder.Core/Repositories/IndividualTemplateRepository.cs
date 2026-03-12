using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>Repository for <see cref="IndividualTemplate"/> entities.</summary>
public class IndividualTemplateRepository : RepositoryBase<IndividualTemplate>
{
    /// <inheritdoc/>
    public IndividualTemplateRepository(AppDbContext context) : base(context) { }

    /// <inheritdoc/>
    protected override IndividualTemplate? FindDuplicate(IndividualTemplate entity) =>
        _context.IndividualTemplates.FirstOrDefault(t =>
            t.ManufacturerId == entity.ManufacturerId &&
            t.TemplateNumber == entity.TemplateNumber &&
            t.DoorMaterialId == entity.DoorMaterialId);
}
