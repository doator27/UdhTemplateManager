using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Defines a strategy for sorting a collection of <see cref="IndividualTemplate"/> records
/// into the order they should appear in an assembled PDF package.
/// </summary>
/// <remarks>
/// Implementations must assume that navigation properties
/// <c>Weight.WeightValue</c> and <c>Manufacturer.ManufacturerName</c> are already loaded.
/// </remarks>
public interface ITemplateSortStrategy
{
    /// <summary>
    /// Sorts the given templates according to the strategy's ordering rules.
    /// </summary>
    /// <param name="templates">The templates to sort.</param>
    /// <returns>A sorted, read-only list of templates.</returns>
    IReadOnlyList<IndividualTemplate> Sort(IEnumerable<IndividualTemplate> templates);
}
