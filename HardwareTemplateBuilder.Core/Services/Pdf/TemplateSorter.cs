using HardwareTemplateBuilder.Core.Models;
using System.Collections.Generic;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Sorts a list of <see cref="IndividualTemplate"/> records using a pluggable
/// <see cref="ITemplateSortStrategy"/>. Follows the Strategy design pattern.
/// </summary>
public class TemplateSorter
{
    private readonly ITemplateSortStrategy _strategy;

    /// <summary>
    /// Initializes a new <see cref="TemplateSorter"/> with the given sort strategy.
    /// </summary>
    /// <param name="strategy">The strategy to use when ordering templates.</param>
    public TemplateSorter(ITemplateSortStrategy strategy)
    {
        _strategy = strategy;
    }

    /// <summary>
    /// Returns the templates sorted according to the configured strategy.
    /// </summary>
    /// <param name="templates">The templates to sort. <c>Manufacturer</c> navigation must be loaded.</param>
    /// <param name="allDescriptions">
    /// Flat dictionary of every description keyed by ID, used to resolve ancestor sort paths.
    /// </param>
    /// <returns>A sorted, read-only list of templates.</returns>
    public IReadOnlyList<IndividualTemplate> Sort(
        IEnumerable<IndividualTemplate> templates,
        IReadOnlyDictionary<int, Description> allDescriptions)
        => _strategy.Sort(templates, allDescriptions);
}
