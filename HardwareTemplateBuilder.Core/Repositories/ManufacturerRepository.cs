using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>Repository for <see cref="Manufacturer"/> entities.</summary>
public class ManufacturerRepository : RepositoryBase<Manufacturer>
{
    /// <inheritdoc/>
    public ManufacturerRepository(AppDbContext context) : base(context) { }

    /// <inheritdoc/>
    protected override Manufacturer? FindDuplicate(Manufacturer entity) =>
        _context.Manufacturers.FirstOrDefault(m => m.ManufacturerName == entity.ManufacturerName);
}
