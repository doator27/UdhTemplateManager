using HardwareTemplateBuilder.Core.Models;

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
    /// <param name="templates">
    /// The templates to sort. Navigation properties
    /// <c>Weight.WeightValue</c> and <c>Manufacturer.ManufacturerName</c> must be loaded.
    /// </param>
    /// <returns>A sorted, read-only list of templates.</returns>
    public IReadOnlyList<IndividualTemplate> Sort(IEnumerable<IndividualTemplate> templates)
        => _strategy.Sort(templates);
}
