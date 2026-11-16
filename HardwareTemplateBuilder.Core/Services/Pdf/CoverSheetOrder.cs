using System;
using System.Collections.Generic;
using System.Linq;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services.Pdf;

/// <summary>
/// Puts hardware rows in the order the generated package's cover sheet lists them, without
/// building the PDF. The cover sheet (see <c>PdfAssemblyService.BuildCoverSheetData</c>) orders
/// items by the first body page their templates land on, and the body follows
/// <see cref="WeightTemplateSortStrategy"/>: template description sort path, then template
/// number. So an item's position is its "earliest" template under that same key, with the
/// row label (custom label, else model number) breaking ties.
/// <para>
/// Items that have no linked templates yet (e.g. new items during bulk add) are placed by their
/// own description's sort path, which is where their templates will normally land; items with
/// neither templates nor a description go last.
/// </para>
/// </summary>
public static class CoverSheetOrder
{
    /// <summary>
    /// Returns <paramref name="items"/> in cover sheet order. The sort is stable.
    /// </summary>
    /// <param name="items">The rows to sort (single manufacturer, as on a per-manufacturer page).</param>
    /// <param name="templatesOf">The templates linked to a row's hardware item for this job (may be empty).</param>
    /// <param name="descriptionIdOf">The row's hardware description, used when it has no templates.</param>
    /// <param name="labelOf">The text shown in the row's Hardware Description column (label, else model number).</param>
    /// <param name="allDescriptions">Every description keyed by ID, to build sort paths.</param>
    public static List<T> Sort<T>(
        IEnumerable<T> items,
        Func<T, IEnumerable<IndividualTemplate>> templatesOf,
        Func<T, int?> descriptionIdOf,
        Func<T, string> labelOf,
        IReadOnlyDictionary<int, Description> allDescriptions)
    {
        return items
            .Select((item, index) => new { Item = item, Index = index, Key = KeyFor(item, templatesOf, descriptionIdOf, allDescriptions) })
            .OrderBy(x => x.Key, SortKeyComparer.Instance)
            .ThenBy(x => labelOf(x.Item) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Index)
            .Select(x => x.Item)
            .ToList();
    }

    /// <summary>
    /// Loads, for each hardware item, the templates that would be included in this job's package:
    /// global links plus links scoped to <paramref name="jobId"/> (same rule as package generation).
    /// </summary>
    public static Dictionary<int, List<IndividualTemplate>> LoadTemplatesByItem(
        AppDbContext context, IEnumerable<int> hardwareItemIds, int jobId)
    {
        var ids = hardwareItemIds.Distinct().ToList();
        return context.HardwareItemTemplates
            .Include(hit => hit.IndividualTemplate)
            .Where(hit => ids.Contains(hit.HardwareItemId) && (hit.JobId == null || hit.JobId == jobId))
            .AsNoTracking()
            .ToList()
            .GroupBy(hit => hit.HardwareItemId)
            .ToDictionary(g => g.Key, g => g.Select(hit => hit.IndividualTemplate).Where(t => t != null).ToList());
    }

    /// <summary>(sort path, template number), or null when the row has nothing to place it by.</summary>
    private sealed record SortKey(IReadOnlyList<int> Path, string TemplateNumber);

    private static SortKey? KeyFor<T>(
        T item,
        Func<T, IEnumerable<IndividualTemplate>> templatesOf,
        Func<T, int?> descriptionIdOf,
        IReadOnlyDictionary<int, Description> allDescriptions)
    {
        var templateKeys = (templatesOf(item) ?? Enumerable.Empty<IndividualTemplate>())
            .Select(t => new SortKey(WeightTemplateSortStrategy.GetSortPath(t.DescriptionId, allDescriptions), t.TemplateNumber ?? string.Empty))
            .ToList();
        if (templateKeys.Count > 0)
            return templateKeys.OrderBy(k => k, SortKeyComparer.Instance).First();

        var descriptionId = descriptionIdOf(item);
        return descriptionId.HasValue && allDescriptions.ContainsKey(descriptionId.Value)
            ? new SortKey(WeightTemplateSortStrategy.GetSortPath(descriptionId.Value, allDescriptions), string.Empty)
            : null;
    }

    /// <summary>Orders by path (as the PDF body does), then template number; null keys last.</summary>
    private sealed class SortKeyComparer : IComparer<SortKey?>
    {
        public static readonly SortKeyComparer Instance = new();

        public int Compare(SortKey? x, SortKey? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return 1;
            if (y == null) return -1;
            var byPath = PathComparer.Instance.Compare(x.Path, y.Path);
            return byPath != 0 ? byPath : StringComparer.OrdinalIgnoreCase.Compare(x.TemplateNumber, y.TemplateNumber);
        }
    }
}
