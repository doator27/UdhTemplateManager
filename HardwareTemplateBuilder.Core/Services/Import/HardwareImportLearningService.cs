using System;
using System.Collections.Generic;
using System.Linq;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>
/// Records what the user decided in the PDF import wizard: each reviewed callout adds one
/// confirmation to its <see cref="PdfImportItemMapping"/> (a different answer replaces it and
/// restarts at 1), and each confirmed manufacturer adds one to that code's
/// <see cref="PdfImportManufacturerAlias"/>. Once a mapping or alias reaches the threshold the
/// import applies it automatically.
/// </summary>
public static class HardwareImportLearningService
{
    /// <summary>Records the user's decisions for reviewed groups.</summary>
    /// <param name="ctx">An open context.</param>
    /// <param name="reviewed">Groups the user reviewed (their chosen fields are read).</param>
    /// <param name="modelNumberOf">Looks up an existing item's (manufacturer id, model number).</param>
    public static void Record(AppDbContext ctx, IEnumerable<HardwareImportGroup> reviewed, Func<int, (int ManufacturerId, string ModelNumber)?> modelNumberOf)
    {
        var now = DateTime.UtcNow;
        foreach (var g in reviewed)
        {
            if (g.SourceKey.Length == 0) continue;
            var code = Truncate(g.ManufacturerCode, 50);
            var key = Truncate(g.SourceKey, 200);

            int? itemId = null, targetMfr = null;
            string? targetModel = null;
            if (!g.Skip)
            {
                if (g.CreateNew)
                {
                    targetMfr = g.NewManufacturerId;
                    targetModel = g.NewModelNumber;
                }
                else if (g.HardwareItemId is int id)
                {
                    itemId = id;
                    var info = modelNumberOf(id);
                    targetMfr = info?.ManufacturerId;
                    targetModel = info?.ModelNumber;
                }
                else
                {
                    continue; // nothing chosen
                }
            }
            var label = string.IsNullOrWhiteSpace(g.Label) ? null : Truncate(g.Label.Trim(), 500);

            var mapping = ctx.PdfImportItemMappings.FirstOrDefault(m => m.ManufacturerCode == code && m.SourceKey == key);
            if (mapping == null)
            {
                mapping = new PdfImportItemMapping { ManufacturerCode = code, SourceKey = key };
                ctx.PdfImportItemMappings.Add(mapping);
            }

            bool same = mapping.ConfirmCount > 0 &&
                        mapping.IsSkip == g.Skip &&
                        (g.Skip || (SameTarget(mapping, itemId, targetMfr, targetModel) &&
                                    string.Equals(mapping.Label ?? string.Empty, label ?? string.Empty, StringComparison.OrdinalIgnoreCase)));
            mapping.ConfirmCount = same ? mapping.ConfirmCount + 1 : 1;
            mapping.IsSkip = g.Skip;
            mapping.HardwareItemId = itemId;
            mapping.TargetManufacturerId = targetMfr;
            mapping.TargetModelNumber = targetModel == null ? null : Truncate(targetModel, 200);
            mapping.Label = label;
            mapping.SourceText = Truncate(g.Catalog, 500);
            mapping.LastConfirmedUtc = now;

            int? mfrForAlias = g.Skip ? null : (g.CreateNew ? g.NewManufacturerId : targetMfr);
            if (code.Length > 0 && mfrForAlias is int mfrId)
            {
                var alias = ctx.PdfImportManufacturerAliases.FirstOrDefault(a => a.Code == code && a.ManufacturerId == mfrId);
                if (alias == null)
                {
                    alias = new PdfImportManufacturerAlias { Code = code, ManufacturerId = mfrId };
                    ctx.PdfImportManufacturerAliases.Add(alias);
                }
                alias.ConfirmCount++;
                alias.LastConfirmedUtc = now;
            }

            ctx.SaveChanges();
        }
    }

    private static bool SameTarget(PdfImportItemMapping m, int? itemId, int? mfr, string? model)
    {
        if (itemId.HasValue && m.HardwareItemId == itemId) return true;
        return m.TargetManufacturerId == mfr && mfr != null &&
               ImportText.Key(m.TargetModelNumber) == ImportText.Key(model) && ImportText.Key(model).Length > 0;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value.Substring(0, max);
}
