using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>How often a word in a default label was removed or kept by the user.</summary>
public sealed class LabelWordStats
{
    /// <summary>Gets or sets how many times the user removed it.</summary>
    public int Removed { get; set; }

    /// <summary>Gets or sets how many times the user kept it.</summary>
    public int Kept { get; set; }
}

/// <summary>What the PDF import has learned from the user's label edits.</summary>
public sealed class LabelLearningData
{
    /// <summary>Gets or sets per word (upper case): how often it was removed or kept.</summary>
    public Dictionary<string, LabelWordStats> Words { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets per hardware item ("mfrId:MODELKEY"): text added in front, with counts.</summary>
    public Dictionary<string, Dictionary<string, int>> Prefixes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets per hardware item: text added at the end, with counts.</summary>
    public Dictionary<string, Dictionary<string, int>> Suffixes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Learns from how the user edits the labels Parse PDF into Bulk Add offers: words they keep
/// removing (e.g. "BAA", "NRP") stop being put in, and text they keep adding in front of or after
/// an item's label (e.g. its description) is added for them. Applies once a word or text has been
/// seen <c>threshold</c> times (the App Setting <c>PdfImportAutoMatchThreshold</c>) and clearly
/// more often than the opposite. Stored as JSON in App Setting <see cref="SettingKey"/>.
/// </summary>
public static class HardwareImportLabelLearning
{
    /// <summary>The <c>AppSetting</c> key the learning is stored under.</summary>
    public const string SettingKey = "PdfImportLabelLearning";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The key of a hardware item for prefixes/suffixes.</summary>
    /// <param name="manufacturerId">Manufacturer.</param>
    /// <param name="modelNumber">Model number.</param>
    public static string ItemKey(int? manufacturerId, string? modelNumber) =>
        $"{manufacturerId?.ToString() ?? "?"}:{ImportText.Key(modelNumber)}";

    /// <summary>Records one label edit (nothing when the label wasn't changed).</summary>
    /// <param name="data">The learning, updated in place.</param>
    /// <param name="offered">The label the app offered.</param>
    /// <param name="final">The label the user kept.</param>
    /// <param name="itemKey">The item (<see cref="ItemKey"/>).</param>
    public static void Observe(LabelLearningData data, string offered, string final, string itemKey)
    {
        offered = ImportText.Tidy(offered ?? string.Empty);
        final = ImportText.Tidy(final ?? string.Empty);
        if (string.Equals(offered, final, StringComparison.Ordinal)) return;

        var finalWords = new HashSet<string>(final.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var w in offered.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool counts = w.Any(char.IsLetter);
            if (finalWords.Contains(w))
            {
                kept.Add(w);
                if (counts) Stats(data, w).Kept++;
            }
            else if (counts)
            {
                Stats(data, w).Removed++;
            }
        }

        // Text added around what was kept: a prefix or a suffix.
        var reduced = string.Join(" ", kept);
        if (reduced.Length == 0 || string.Equals(final, reduced, StringComparison.OrdinalIgnoreCase)) return;
        if (final.EndsWith(reduced, StringComparison.OrdinalIgnoreCase))
            Count(data.Prefixes, itemKey, final[..^reduced.Length].Trim());
        else if (final.StartsWith(reduced, StringComparison.OrdinalIgnoreCase))
            Count(data.Suffixes, itemKey, final[reduced.Length..].Trim());
    }

    /// <summary>Applies what was learned to a label the app is about to offer.</summary>
    /// <param name="data">The learning.</param>
    /// <param name="label">The default label.</param>
    /// <param name="itemKey">The item (<see cref="ItemKey"/>).</param>
    /// <param name="threshold">How many times something must be seen before it is applied.</param>
    public static string Apply(LabelLearningData data, string label, string itemKey, int threshold)
    {
        var words = ImportText.Tidy(label ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !(data.Words.TryGetValue(w, out var s) && s.Removed >= threshold && s.Removed >= 3 * s.Kept))
            .ToList();
        var result = string.Join(" ", words);
        if (Best(data.Prefixes, itemKey, threshold) is { } prefix && !result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            result = $"{prefix} {result}".Trim();
        if (Best(data.Suffixes, itemKey, threshold) is { } suffix && !result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            result = $"{result} {suffix}".Trim();
        return result;
    }

    /// <summary>Loads the learning (empty when none or unreadable).</summary>
    /// <param name="ctx">An open context.</param>
    public static LabelLearningData Load(AppDbContext ctx)
    {
        var raw = new AppSettingRepository(ctx).GetValue(SettingKey);
        if (string.IsNullOrWhiteSpace(raw)) return new LabelLearningData();
        try
        {
            var data = JsonSerializer.Deserialize<LabelLearningData>(raw, Json) ?? new LabelLearningData();
            data.Words = new Dictionary<string, LabelWordStats>(data.Words, StringComparer.OrdinalIgnoreCase);
            data.Prefixes = new Dictionary<string, Dictionary<string, int>>(data.Prefixes, StringComparer.OrdinalIgnoreCase);
            data.Suffixes = new Dictionary<string, Dictionary<string, int>>(data.Suffixes, StringComparer.OrdinalIgnoreCase);
            return data;
        }
        catch (JsonException)
        {
            return new LabelLearningData();
        }
    }

    /// <summary>Saves the learning.</summary>
    /// <param name="ctx">An open context.</param>
    /// <param name="data">The learning.</param>
    public static void Save(AppDbContext ctx, LabelLearningData data) =>
        new AppSettingRepository(ctx).SetValue(SettingKey, JsonSerializer.Serialize(data, Json));

    private static LabelWordStats Stats(LabelLearningData data, string word)
    {
        var key = word.ToUpperInvariant();
        if (!data.Words.TryGetValue(key, out var s)) data.Words[key] = s = new LabelWordStats();
        return s;
    }

    private static void Count(Dictionary<string, Dictionary<string, int>> map, string itemKey, string text)
    {
        if (text.Length == 0) return;
        if (!map.TryGetValue(itemKey, out var counts)) map[itemKey] = counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        counts[text] = counts.GetValueOrDefault(text) + 1;
    }

    private static string? Best(Dictionary<string, Dictionary<string, int>> map, string itemKey, int threshold)
    {
        if (!map.TryGetValue(itemKey, out var counts) || counts.Count == 0) return null;
        var ordered = counts.OrderByDescending(c => c.Value).ToList();
        var best = ordered[0];
        int others = ordered.Skip(1).Sum(c => c.Value);
        return best.Value >= threshold && best.Value >= 3 * others ? best.Key : null;
    }
}
