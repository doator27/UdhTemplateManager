using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>
/// Repository for <see cref="DoorMaterial"/> entities.
/// DoorMaterial values are seeded and constrained to "Metal", "Wood", and "Both" only.
/// </summary>
public class DoorMaterialRepository : RepositoryBase<DoorMaterial>
{
    /// <inheritdoc/>
    public DoorMaterialRepository(AppDbContext context) : base(context) { }

    /// <inheritdoc/>
    protected override DoorMaterial? FindDuplicate(DoorMaterial entity) =>
        _context.DoorMaterials.FirstOrDefault(dm => dm.Material == entity.Material);
}
