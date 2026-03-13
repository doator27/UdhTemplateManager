using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Sorts templates by manufacturer group and weight, following the package assembly rules:
/// within a group sort by <c>Weight</c> ascending; between groups sort by the group's
/// lowest weight, with alphabetical <c>ManufacturerName</c> as a tiebreaker.
/// </summary>
public class WeightTemplateSortStrategy : ITemplateSortStrategy
{
    private readonly WeightParser _weightParser;

    /// <summary>
    /// Initializes a new <see cref="WeightTemplateSortStrategy"/> with the given weight parser.
    /// </summary>
    /// <param name="weightParser">Parser used to compare weight strings.</param>
    public WeightTemplateSortStrategy(WeightParser weightParser)
    {
        _weightParser = weightParser;
    }

    /// <inheritdoc/>
    public IReadOnlyList<IndividualTemplate> Sort(IEnumerable<IndividualTemplate> templates)
    {
        // Group by manufacturer, sort within each group by weight ascending.
        var groups = templates
            .GroupBy(t => t.ManufacturerId)
            .Select(g =>
            {
                var sortedTemplates = g
                    .OrderBy(t => _weightParser.Parse(t.Description.WeightValue))
                    .ToList();

                var minWeight = sortedTemplates
                    .Select(t => _weightParser.Parse(t.Description.WeightValue))
                    .Min();

                return new
                {
                    ManufacturerName = g.First().Manufacturer.ManufacturerName,
                    Templates = sortedTemplates,
                    MinWeight = minWeight
                };
            })
            .OrderBy(g => g.MinWeight)
            .ThenBy(g => g.ManufacturerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return groups.SelectMany(g => g.Templates).ToList().AsReadOnly();
    }
}
