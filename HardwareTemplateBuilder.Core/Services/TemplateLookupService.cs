using System.Collections.Generic;
using System.Linq;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>Result record returned by <see cref="TemplateLookupService.Search"/>.</summary>
public record TemplateLookupResult(
    int TemplateId,
    string Manufacturer,
    string Description,
    string TemplateNumber,
    string? LocalLink,
    string? OnlineLink);

/// <summary>
/// Read-only service that searches globally-visible <see cref="IndividualTemplate"/> records
/// by manufacturer, description, and hardware item model number.
/// </summary>
public class TemplateLookupService
{
    private readonly AppDbContext _context;

    /// <summary>Initializes a new <see cref="TemplateLookupService"/>.</summary>
    public TemplateLookupService(AppDbContext context) => _context = context;

    /// <summary>
    /// Searches globally-visible templates by manufacturer, description, and model number.
    /// When a model number is supplied, it is matched as a substring against linked
    /// <see cref="HardwareItem"/> model numbers. If no hardware item matches, all templates
    /// satisfying the remaining filters are returned as a "similar" fallback and
    /// <c>IsExactMatch</c> is set to <c>false</c>.
    /// </summary>
    /// <param name="manufacturerId">Filter by manufacturer ID, or null for any.</param>
    /// <param name="descriptionId">Filter by description ID, or null for any.</param>
    /// <param name="modelNumber">Substring match against linked hardware item model numbers.</param>
    /// <returns>Matched results and a flag indicating whether the match was exact.</returns>
    public (IReadOnlyList<TemplateLookupResult> Results, bool IsExactMatch) Search(
        int? manufacturerId, int? descriptionId, string? modelNumber)
    {
        IQueryable<IndividualTemplate> baseQuery = _context.IndividualTemplates
            .Where(t => t.OriginJobId == null);

        if (manufacturerId.HasValue)
            baseQuery = baseQuery.Where(t => t.ManufacturerId == manufacturerId.Value);
        if (descriptionId.HasValue)
            baseQuery = baseQuery.Where(t => t.DescriptionId == descriptionId.Value);

        if (!string.IsNullOrWhiteSpace(modelNumber))
        {
            var linkedIds = _context.HardwareItemTemplates
                .Where(hit => hit.HardwareItem.ModelNumber.Contains(modelNumber))
                .Select(hit => hit.IndividualTemplateId)
                .Distinct()
                .ToList();

            if (linkedIds.Count > 0)
                return (Project(baseQuery.Where(t => linkedIds.Contains(t.Id))), true);

            // No hardware items matched — fall back to all templates for the other filters.
            return (Project(baseQuery), false);
        }

        return (Project(baseQuery), true);
    }

    private IReadOnlyList<TemplateLookupResult> Project(IQueryable<IndividualTemplate> query) =>
        query
            .OrderBy(t => t.Manufacturer.ManufacturerName)
            .ThenBy(t => t.TemplateNumber)
            .Select(t => new TemplateLookupResult(
                t.Id,
                t.Manufacturer.ManufacturerName,
                t.Description.DescriptionText,
                t.TemplateNumber,
                t.LocalLink,
                t.OnlineLink))
            .ToList();
}
