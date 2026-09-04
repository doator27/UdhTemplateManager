using HardwareTemplateBuilder.Core.Models;
using System.Collections.Generic;

namespace HardwareTemplateBuilder.Core.Repositories;

/// <summary>
/// Specialized repository interface for <see cref="HardwareItem"/> entities,
/// adding a frequency-aware search method.
/// </summary>
public interface IHardwareItemRepository : IRepository<HardwareItem>
{
    /// <summary>
    /// Searches hardware items by manufacturer name, description ID, and model number.
    /// Any parameter may be null or empty to skip that filter.
    /// Results are sorted by <see cref="HardwareItem.Frequency"/> descending (most-used first).
    /// </summary>
    /// <param name="manufacturerName">Optional manufacturer name filter (partial match).</param>
    /// <param name="descriptionId">Optional description ID filter (exact match).</param>
    /// <param name="modelNumber">Optional model number filter (partial match).</param>
    /// <returns>Matching hardware items ordered by frequency descending.</returns>
    IEnumerable<HardwareItem> Search(string? manufacturerName, int? descriptionId, string? modelNumber);

    /// <summary>
    /// Searches hardware items by manufacturer name, description ID, and model number,
    /// filtering by active status.
    /// Any parameter may be null or empty to skip that filter.
    /// Results are sorted by <see cref="HardwareItem.Frequency"/> descending (most-used first).
    /// </summary>
    /// <param name="manufacturerName">Optional manufacturer name filter (partial match).</param>
    /// <param name="descriptionId">Optional description ID filter (exact match).</param>
    /// <param name="modelNumber">Optional model number filter (partial match).</param>
    /// <param name="activeOnly">If true, only returns active items; if false, returns all items.</param>
    /// <returns>Matching hardware items ordered by frequency descending.</returns>
    IEnumerable<HardwareItem> Search(string? manufacturerName, int? descriptionId, string? modelNumber, bool activeOnly);

    /// <summary>
    /// Copies all hardware items from the source manufacturer to the target manufacturer.
    /// The original items remain unchanged (still owned by the source manufacturer), so the
    /// same item effectively exists under both manufacturer names after the copy.
    /// Items that already exist for the target manufacturer (same description and model number)
    /// are skipped to avoid duplicates.
    /// </summary>
    /// <param name="sourceManufacturerId">The manufacturer to copy items from.</param>
    /// <param name="targetManufacturerId">The manufacturer to copy items to.</param>
    /// <returns>The number of items that were copied.</returns>
    int CopyItemsToManufacturer(int sourceManufacturerId, int targetManufacturerId);
}
