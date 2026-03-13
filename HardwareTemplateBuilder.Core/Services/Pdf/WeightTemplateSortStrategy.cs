using HardwareTemplateBuilder.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Sorts templates by description sort order within manufacturer groups, then orders groups
/// by their lowest description sort order with alphabetical <c>ManufacturerName</c> as a tiebreaker.
/// </summary>
public class WeightTemplateSortStrategy : ITemplateSortStrategy
{
    /// <inheritdoc/>
    public IReadOnlyList<IndividualTemplate> Sort(IEnumerable<IndividualTemplate> templates)
    {
        var groups = templates
            .GroupBy(t => t.ManufacturerId)
            .Select(g =>
            {
                var sortedTemplates = g
                    .OrderBy(t => t.Description.SortOrder)
                    .ThenBy(t => t.TemplateNumber, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var minOrder = sortedTemplates.Min(t => t.Description.SortOrder);

                return new
                {
                    ManufacturerName = g.First().Manufacturer.ManufacturerName,
                    Templates = sortedTemplates,
                    MinOrder = minOrder
                };
            })
            .OrderBy(g => g.MinOrder)
            .ThenBy(g => g.ManufacturerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return groups.SelectMany(g => g.Templates).ToList().AsReadOnly();
    }
}
