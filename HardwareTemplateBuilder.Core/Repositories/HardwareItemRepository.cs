using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>
/// Repository for <see cref="HardwareItem"/> entities with frequency-aware search support.
/// </summary>
public class HardwareItemRepository : RepositoryBase<HardwareItem>, IHardwareItemRepository
{
    /// <inheritdoc/>
    public HardwareItemRepository(AppDbContext context) : base(context) { }

    /// <inheritdoc/>
    protected override HardwareItem? FindDuplicate(HardwareItem entity) =>
        _context.HardwareItems.FirstOrDefault(h =>
            h.ManufacturerId == entity.ManufacturerId &&
            h.DescriptionId == entity.DescriptionId &&
            h.ModelNumber == entity.ModelNumber);

    /// <inheritdoc/>
    public IEnumerable<HardwareItem> Search(string? manufacturerName, int? descriptionId, string? modelNumber)
    {
        var query = _context.HardwareItems
            .Include(h => h.Manufacturer)
            .Include(h => h.Description)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(manufacturerName))
            query = query.Where(h => h.Manufacturer.ManufacturerName.Contains(manufacturerName));

        if (descriptionId.HasValue)
            query = query.Where(h => h.DescriptionId == descriptionId.Value);

        if (!string.IsNullOrWhiteSpace(modelNumber))
            query = query.Where(h => h.ModelNumber.Contains(modelNumber));

        return query.OrderByDescending(h => h.Frequency).ToList();
    }
}
