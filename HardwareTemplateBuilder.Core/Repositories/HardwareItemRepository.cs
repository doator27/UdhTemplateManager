using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;
using System;
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
    /// <remarks>
    /// Compares <see cref="HardwareItem.ModelNumber"/> case-insensitively and trimmed so that
    /// whitespace or casing differences (e.g. "L9000" vs "l9000 ") don't create a second,
    /// visually-identical <see cref="HardwareItem"/> row. Without this, a duplicate row with no
    /// linked templates can end up alongside the real one with templates, and whichever row a
    /// search picks up (e.g. by frequency ordering) determines whether templates are found.
    /// </remarks>
    protected override HardwareItem? FindDuplicate(HardwareItem entity)
    {
        var normalizedModelNumber = entity.ModelNumber?.Trim() ?? string.Empty;
        return _context.HardwareItems
            .Where(h =>
                h.ManufacturerId == entity.ManufacturerId &&
                h.DescriptionId == entity.DescriptionId)
            .AsEnumerable()
            .FirstOrDefault(h =>
                string.Equals(h.ModelNumber?.Trim(), normalizedModelNumber, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc/>
    public IEnumerable<HardwareItem> Search(string? manufacturerName, int? descriptionId, string? modelNumber)
    {
        return Search(manufacturerName, descriptionId, modelNumber, activeOnly: true);
    }

    /// <inheritdoc/>
    public IEnumerable<HardwareItem> Search(string? manufacturerName, int? descriptionId, string? modelNumber, bool activeOnly)
    {
        var query = _context.HardwareItems
            .Include(h => h.Manufacturer)
            .Include(h => h.Description)
            .AsQueryable();

        if (activeOnly)
            query = query.Where(h => h.IsActive && h.Description.IsActive);

        if (!string.IsNullOrWhiteSpace(manufacturerName))
            query = query.Where(h => h.Manufacturer.ManufacturerName.Contains(manufacturerName));

        if (descriptionId.HasValue)
            query = query.Where(h => h.DescriptionId == descriptionId.Value);

        if (!string.IsNullOrWhiteSpace(modelNumber))
            query = query.Where(h => h.ModelNumber.Contains(modelNumber));

        return query.OrderByDescending(h => h.Frequency).ToList();
    }

    /// <inheritdoc/>
    public int CopyItemsToManufacturer(int sourceManufacturerId, int targetManufacturerId)
    {
        if (sourceManufacturerId == targetManufacturerId)
            return 0;

        var sourceItems = _context.HardwareItems
            .Where(h => h.ManufacturerId == sourceManufacturerId)
            .ToList();

        var existingTargetItems = _context.HardwareItems
            .Where(h => h.ManufacturerId == targetManufacturerId)
            .Select(h => new { h.DescriptionId, h.ModelNumber })
            .ToHashSet();

        var copiedCount = 0;
        foreach (var sourceItem in sourceItems)
        {
            if (existingTargetItems.Any(e => e.DescriptionId == sourceItem.DescriptionId && e.ModelNumber == sourceItem.ModelNumber))
                continue;

            _context.HardwareItems.Add(new HardwareItem
            {
                ManufacturerId = targetManufacturerId,
                DescriptionId = sourceItem.DescriptionId,
                ModelNumber = sourceItem.ModelNumber,
                Remarks = sourceItem.Remarks,
                IsActive = sourceItem.IsActive,
                Frequency = 0,
            });
            copiedCount++;
        }

        if (copiedCount > 0)
            _context.SaveChanges();

        return copiedCount;
    }
}
