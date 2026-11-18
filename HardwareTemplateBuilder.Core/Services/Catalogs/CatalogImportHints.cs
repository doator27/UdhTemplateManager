using System.Collections.Generic;
using System.Linq;
using HardwareTemplateBuilder.Core.Services.Import;

namespace HardwareTemplateBuilder.Core.Services.Catalogs;

/// <summary>
/// Adds product catalog help to "Parse PDF into Bulk Add": for each callout that needs review,
/// looks it up in its manufacturer's catalogs and keeps the result on the group (shown in the
/// wizard), suggests the hardware items that already use a template the catalog names, and
/// pre-picks a description for a new item from the catalog wording. Never matches automatically.
/// </summary>
public static class CatalogImportHints
{
    /// <summary>Score of a "catalog names its template" suggestion (below exact model/label matches).</summary>
    public const int SuggestionScore = 35000;

    /// <summary>Applies the hints.</summary>
    /// <param name="groups">The matched groups.</param>
    /// <param name="lookup">Catalog lookups.</param>
    /// <param name="knowledge">The import's view of the database (active items).</param>
    public static void Apply(IEnumerable<HardwareImportGroup> groups, CatalogLookupService lookup, HardwareImportKnowledge knowledge)
    {
        foreach (var g in groups)
        {
            if (g.Status != ImportMatchStatus.NeedsReview) continue;
            var mfr = g.ManufacturerId ?? g.NewManufacturerId;
            if (mfr is not int manufacturerId || !lookup.HasCatalogs(manufacturerId)) continue;

            var result = lookup.Lookup(manufacturerId, g.Catalog);
            if (!result.Found) continue;
            g.CatalogInfo = result;
            g.NewDescriptionId ??= result.SuggestedDescriptionId;

            bool hadChoice = g.HardwareItemId != null;
            foreach (var t in result.Templates)
            {
                foreach (var itemId in t.HardwareItemIds)
                {
                    if (!knowledge.Items.TryGetValue(itemId, out var item) || item.ManufacturerId != manufacturerId) continue;
                    if (g.Suggestions.Any(s => s.HardwareItemId == itemId)) continue;
                    g.Suggestions.Add(new ImportSuggestion(itemId, null,
                        $"Catalog page {t.PageNumber} gives template {t.Number}, which this item uses", SuggestionScore));
                }
            }
            var sorted = g.Suggestions.OrderByDescending(s => s.Score).Take(8).ToList();
            g.Suggestions.Clear();
            g.Suggestions.AddRange(sorted);

            if (!hadChoice && g.Suggestions.FirstOrDefault() is { } top)
            {
                g.HardwareItemId = top.HardwareItemId;
                g.Label = top.Label ?? HardwareImportMatcher.DefaultLabel(g.Catalog, knowledge.Items[top.HardwareItemId].ModelNumber);
                if (g.Reason == "Not found") g.Reason = "Suggested from the product catalog, please confirm";
            }
        }
    }
}
