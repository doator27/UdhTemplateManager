using HardwareTemplateBuilder.Core.Models;
using System.Collections.Generic;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Defines a strategy for sorting a collection of <see cref="IndividualTemplate"/> records
/// into the order they should appear in an assembled PDF package.
/// </summary>
public interface ITemplateSortStrategy
{
    /// <summary>
    /// Sorts the given templates according to the strategy's ordering rules.
    /// </summary>
    /// <param name="templates">The templates to sort. Navigation property <c>Manufacturer</c> must be loaded.</param>
    /// <param name="allDescriptions">
    /// Flat dictionary of every description in the system keyed by ID, used to walk the
    /// ancestor chain and compute full-path sort keys.
    /// </param>
    /// <returns>A sorted, read-only list of templates.</returns>
    IReadOnlyList<IndividualTemplate> Sort(
        IEnumerable<IndividualTemplate> templates,
        IReadOnlyDictionary<int, Description> allDescriptions);
}
