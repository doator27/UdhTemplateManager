using System;
using System.Collections.Generic;
using System.Linq;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>A hardware item as the matcher sees it.</summary>
/// <param name="Id">Hardware item id.</param>
/// <param name="ManufacturerId">Manufacturer id.</param>
/// <param name="DescriptionId">Description id.</param>
/// <param name="ModelNumber">Model number.</param>
public sealed record ImportItemInfo(int Id, int ManufacturerId, int DescriptionId, string ModelNumber);

/// <summary>A label (JobHardware.CustomDescription) used before on an item, and how often.</summary>
/// <param name="HardwareItemId">The item it was used on.</param>
/// <param name="Label">The label text as typed.</param>
/// <param name="Key">Its comparison key.</param>
/// <param name="Count">How many job lines used it.</param>
public sealed record ImportLabelUse(int HardwareItemId, string Label, string Key, int Count);

/// <summary>
/// Everything the PDF import matches against, loaded once per import: manufacturers, active
/// hardware items, every label used on past jobs, and what the import has learned
/// (<see cref="PdfImportItemMapping"/>, <see cref="PdfImportManufacturerAlias"/>).
/// </summary>
public sealed class HardwareImportKnowledge
{
    /// <summary>App Setting key for how many confirmations make a learned match automatic.</summary>
    public const string ThresholdSettingKey = "PdfImportAutoMatchThreshold";

    /// <summary>Default for <see cref="ThresholdSettingKey"/>.</summary>
    public const int DefaultThreshold = 5;

    /// <summary>Gets the manufacturers (id → name).</summary>
    public Dictionary<int, string> Manufacturers { get; init; } = new();

    /// <summary>Gets the active hardware items by id.</summary>
    public Dictionary<int, ImportItemInfo> Items { get; init; } = new();

    /// <summary>Gets past labels grouped by comparison key.</summary>
    public Dictionary<string, List<ImportLabelUse>> LabelsByKey { get; init; } = new();

    /// <summary>Gets learned callout mappings keyed by (manufacturer code, source key).</summary>
    public Dictionary<(string Code, string Key), PdfImportItemMapping> Mappings { get; init; } = new();

    /// <summary>Gets learned manufacturer aliases by code.</summary>
    public Dictionary<string, List<PdfImportManufacturerAlias>> Aliases { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the number of confirmations that makes a learned match automatic.</summary>
    public int Threshold { get; init; } = DefaultThreshold;

    /// <summary>Loads everything from the database.</summary>
    /// <param name="ctx">An open context.</param>
    public static HardwareImportKnowledge Load(AppDbContext ctx)
    {
        var items = ctx.HardwareItems
            .Where(h => h.IsActive)
            .Select(h => new ImportItemInfo(h.Id, h.ManufacturerId, h.DescriptionId, h.ModelNumber))
            .ToList()
            .ToDictionary(i => i.Id);

        var labelRows = ctx.JobHardware
            .Where(jh => jh.CustomDescription != null && jh.CustomDescription != "")
            .GroupBy(jh => new { jh.HardwareItemId, jh.CustomDescription })
            .Select(g => new { g.Key.HardwareItemId, Label = g.Key.CustomDescription!, Count = g.Count() })
            .ToList();
        var labels = labelRows
            .Where(r => items.ContainsKey(r.HardwareItemId))
            .Select(r => new ImportLabelUse(r.HardwareItemId, r.Label, ImportText.Key(r.Label), r.Count))
            .Where(l => l.Key.Length > 0)
            .GroupBy(l => l.Key)
            .ToDictionary(g => g.Key, g => g.ToList());

        int threshold = DefaultThreshold;
        try
        {
            var raw = new AppSettingRepository(ctx).GetValue(ThresholdSettingKey);
            if (int.TryParse(raw, out var t) && t >= 1) threshold = t;
        }
        catch
        {
            // Missing setting → default.
        }

        return new HardwareImportKnowledge
        {
            Manufacturers = ctx.Manufacturers.ToDictionary(m => m.Id, m => m.ManufacturerName),
            Items = items,
            LabelsByKey = labels,
            Mappings = ctx.PdfImportItemMappings.ToList()
                .GroupBy(m => (m.ManufacturerCode, m.SourceKey))
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.LastConfirmedUtc).First()),
            Aliases = ctx.PdfImportManufacturerAliases.ToList()
                .GroupBy(a => a.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase),
            Threshold = threshold,
        };
    }
}
