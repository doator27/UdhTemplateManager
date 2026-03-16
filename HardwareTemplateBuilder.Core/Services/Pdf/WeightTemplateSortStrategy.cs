using HardwareTemplateBuilder.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Sorts templates by the full ancestor path of their description (root → … → leaf sort orders),
/// then by template number within the same path. Manufacturer groups are ordered by the minimum
/// path found in the group, with alphabetical <c>ManufacturerName</c> as a tiebreaker.
/// </summary>
public class WeightTemplateSortStrategy : ITemplateSortStrategy
{
    /// <inheritdoc/>
    public IReadOnlyList<IndividualTemplate> Sort(
        IEnumerable<IndividualTemplate> templates,
        IReadOnlyDictionary<int, Description> allDescriptions)
    {
        var groups = templates
            .GroupBy(t => t.ManufacturerId)
            .Select(g =>
            {
                var sortedTemplates = g
                    .OrderBy(t => GetSortPath(t.DescriptionId, allDescriptions), PathComparer.Instance)
                    .ThenBy(t => t.TemplateNumber, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var minPath = sortedTemplates
                    .Select(t => GetSortPath(t.DescriptionId, allDescriptions))
                    .Min(PathComparer.Instance)!;

                return new
                {
                    ManufacturerName = g.First().Manufacturer?.ManufacturerName ?? string.Empty,
                    Templates        = sortedTemplates,
                    MinPath          = minPath
                };
            })
            .OrderBy(g => g.MinPath, PathComparer.Instance)
            .ThenBy(g => g.ManufacturerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return groups.SelectMany(g => g.Templates).ToList().AsReadOnly();
    }

    /// <summary>
    /// Walks the ancestor chain of <paramref name="descriptionId"/> and returns the ordered
    /// list of <c>SortOrder</c> values from the root down to the node.
    /// Exposed as <c>internal static</c> so other services (e.g. cover sheet sorting) can
    /// reuse the same path-building logic without duplicating code.
    /// </summary>
    internal static IReadOnlyList<int> GetSortPath(
        int descriptionId,
        IReadOnlyDictionary<int, Description> allDescriptions)
    {
        var path = new List<int>();
        int? currentId = descriptionId;

        while (currentId.HasValue && allDescriptions.TryGetValue(currentId.Value, out var desc))
        {
            path.Insert(0, desc.SortOrder);
            currentId = desc.ParentId;
        }

        return path.Count > 0 ? path : new List<int> { 0 };
    }
}

/// <summary>
/// Lexicographic comparer for sort-path lists. Shorter paths that are a prefix of a longer
/// path sort before the longer path (parent before children).
/// </summary>
internal sealed class PathComparer : IComparer<IReadOnlyList<int>>
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly PathComparer Instance = new();

    /// <inheritdoc/>
    public int Compare(IReadOnlyList<int>? x, IReadOnlyList<int>? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        var len = Math.Min(x.Count, y.Count);
        for (int i = 0; i < len; i++)
        {
            var cmp = x[i].CompareTo(y[i]);
            if (cmp != 0) return cmp;
        }
        return x.Count.CompareTo(y.Count);
    }
}
