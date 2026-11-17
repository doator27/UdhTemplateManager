using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>
/// Groups parsed PDF lines into distinct callouts and works out which hardware item (and label)
/// each one is, using the database (<see cref="HardwareImportKnowledge"/>):
/// <list type="number">
/// <item>Manufacturer: learned code alias → the PDF's own code list → common industry codes →
/// a manufacturer name written in the callout (e.g. "8080-T-W 3/4" GRI").</item>
/// <item>Item: a learned mapping confirmed at least <see cref="HardwareImportKnowledge.Threshold"/>
/// times → an exact model number → an exact label used on a past job. Those are
/// <see cref="ImportMatchStatus.Matched"/>; everything else is <see cref="ImportMatchStatus.NeedsReview"/>
/// with ranked suggestions (labels the callout starts with, model numbers it starts with, …).</item>
/// </list>
/// The label is the callout itself (sizes removed), a past label when one matches, or blank when
/// the callout is just the model number, which mirrors how jobs are entered by hand.
/// </summary>
public static class HardwareImportMatcher
{
    /// <summary>
    /// Common manufacturer codes used by hardware schedule software, by code with trailing digits
    /// removed where that's unambiguous ("MK01" → "MK"). Values are matched to manufacturer names.
    /// </summary>
    private static readonly Dictionary<string, string[]> CommonCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IV"] = new[] { "Ives" }, ["LC"] = new[] { "LCN" }, ["ZE"] = new[] { "Zero" }, ["PE"] = new[] { "Pemko" },
        ["SC"] = new[] { "Schlage" }, ["VO"] = new[] { "Von Duprin" }, ["RO"] = new[] { "Rockwood" },
        ["GL"] = new[] { "Glynn Johnson" }, ["NA"] = new[] { "NGP" }, ["NG"] = new[] { "NGP" }, ["AB"] = new[] { "ABH" },
        ["MK"] = new[] { "Marks" }, ["SA"] = new[] { "Sargent" }, ["HA"] = new[] { "Hager" }, ["MC"] = new[] { "McKinney" },
        ["TR"] = new[] { "Trimco" }, ["RX"] = new[] { "Rixson" }, ["NO"] = new[] { "Norton" }, ["DO"] = new[] { "Dorma" },
        ["BE"] = new[] { "Best" }, ["FA"] = new[] { "Falcon" }, ["CR"] = new[] { "Corbin Russwin" }, ["SU"] = new[] { "Securitron" },
        ["HE"] = new[] { "HES" }, ["ST"] = new[] { "Stanley" }, ["AC"] = new[] { "Accurate" }, ["PR"] = new[] { "Precision" },
        ["SD"] = new[] { "SDC" }, ["AD"] = new[] { "Adams Rite" }, ["DE"] = new[] { "Detex" }, ["BA"] = new[] { "Baldwin" },
        ["EM"] = new[] { "Emtek" }, ["RI"] = new[] { "Richelieu" }, ["S501"] = new[] { "Salto" }, ["S701"] = new[] { "Simonswerk" },
        ["TE"] = new[] { "Tectus" }, ["SG"] = new[] { "Sugatsune" }, ["FS"] = new[] { "FSB" }, ["GR"] = new[] { "GRI" },
    };

    private static readonly HashSet<string> NameNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "INC", "CO", "COMPANY", "MANUFACTURING", "MFG", "PRODUCTS", "CLOSERS", "LOCKS", "LOCK", "HARDWARE", "NORTH",
        "AMERICA", "SYSTEMS", "ELECTRONICS", "THE", "CORP", "LLC", "LTD", "INDUSTRIES", "GROUP",
    };

    /// <summary>Groups lines into distinct callouts and resolves each one.</summary>
    /// <param name="lines">Parsed lines.</param>
    /// <param name="knowledge">What the database knows.</param>
    public static List<HardwareImportGroup> Match(IEnumerable<ParsedHardwareLine> lines, HardwareImportKnowledge knowledge)
    {
        var groups = new List<HardwareImportGroup>();
        var byKey = new Dictionary<(string, string), HardwareImportGroup>();
        foreach (var line in lines)
        {
            var catalog = ImportText.Tidy(line.Catalog);
            var key = ImportText.Key(catalog);
            if (key.Length == 0) continue;
            var groupKey = (line.ManufacturerCode, key);
            if (!byKey.TryGetValue(groupKey, out var group))
            {
                group = new HardwareImportGroup
                {
                    ManufacturerCode = line.ManufacturerCode,
                    PdfManufacturerName = line.ManufacturerName,
                    GenericName = ImportText.Tidy(line.GenericName),
                    Catalog = catalog,
                    SourceKey = key,
                };
                byKey[groupKey] = group;
                groups.Add(group);
            }
            group.Lines.Add(line);
        }

        foreach (var group in groups) Resolve(group, knowledge);
        return groups;
    }

    /// <summary>
    /// For layouts the parser doesn't know (e.g. shop drawings): finds words in the text that are
    /// exactly a known model number or past label (letters + digits, 4+ characters) and returns
    /// them as lines with no manufacturer code, so only confident matches come through.
    /// </summary>
    /// <param name="pages">Layout lines per page.</param>
    /// <param name="knowledge">What the database knows.</param>
    public static List<ParsedHardwareLine> FindKnownCallouts(IReadOnlyList<IReadOnlyList<string>> pages, HardwareImportKnowledge knowledge)
    {
        var known = new HashSet<string>(knowledge.LabelsByKey.Keys);
        foreach (var item in knowledge.Items.Values) known.Add(ImportText.Key(item.ModelNumber));

        var result = new List<ParsedHardwareLine>();
        var seen = new HashSet<string>();
        for (int p = 0; p < pages.Count; p++)
        {
            foreach (var line in pages[p])
            {
                foreach (Match m in Regex.Matches(line, @"[A-Za-z0-9][A-Za-z0-9\-./]{3,}"))
                {
                    var token = m.Value.Trim('.', '-', '/');
                    if (!token.Any(char.IsLetter) || !token.Any(char.IsDigit)) continue;
                    var key = ImportText.Key(token);
                    if (key.Length < 4 || !known.Contains(key) || !seen.Add(key)) continue;
                    result.Add(new ParsedHardwareLine { Catalog = token, Page = p + 1, Quantity = 0 });
                }
            }
        }
        return result;
    }

    // ── Resolution ──────────────────────────────────────────────────────────

    private static void Resolve(HardwareImportGroup group, HardwareImportKnowledge k)
    {
        group.ManufacturerId = ResolveManufacturer(group, k);
        var fullKey = group.SourceKey;
        var shortKey = ImportText.Key(ImportText.StripSize(group.Catalog));
        var pool = k.Items.Values.Where(i => group.ManufacturerId == null || i.ManufacturerId == group.ManufacturerId).ToList();
        var suggestions = new Dictionary<(int, string?), ImportSuggestion>();

        void Suggest(int itemId, string? label, string reason, int score)
        {
            var key = (itemId, string.IsNullOrWhiteSpace(label) ? null : label);
            if (!suggestions.TryGetValue(key, out var existing) || existing.Score < score)
                suggestions[key] = new ImportSuggestion(itemId, key.Item2, reason, score);
        }

        // 1. Learned mapping.
        if (k.Mappings.TryGetValue((group.ManufacturerCode, fullKey), out var mapping))
        {
            int? itemId = ResolveMappingItem(mapping, k);
            bool trusted = mapping.ConfirmCount >= k.Threshold;
            if (mapping.IsSkip && trusted)
            {
                group.Status = ImportMatchStatus.Skipped;
                group.Skip = true;
                group.Reason = $"Skipped (you skipped it {mapping.ConfirmCount} times)";
                return;
            }
            if (itemId.HasValue && trusted)
            {
                Choose(group, itemId.Value, mapping.Label ?? string.Empty, $"Learned ({mapping.ConfirmCount} confirmations)", k);
                return;
            }
            if (itemId.HasValue)
                Suggest(itemId.Value, mapping.Label, $"Your earlier choice ({mapping.ConfirmCount} of {k.Threshold})", 100000 + mapping.ConfirmCount);
            else if (mapping.IsSkip)
                group.Reason = $"You skipped this before ({mapping.ConfirmCount} of {k.Threshold})";
        }

        // 2./3. Exact model number or exact past label (only automatic when the manufacturer is known,
        // or the match is unique across manufacturers).
        foreach (var key in new[] { fullKey, shortKey }.Distinct())
        {
            if (key.Length == 0) continue;
            var exactModel = (group.ManufacturerId != null ? pool : k.Items.Values.ToList())
                .Where(i => ImportText.Key(i.ModelNumber) == key).ToList();
            if (exactModel.Count == 1 || (exactModel.Count > 0 && group.ManufacturerId != null))
            {
                if (suggestions.Count == 0)
                {
                    Choose(group, exactModel[0].Id, string.Empty, "Model number", k);
                    return;
                }
                Suggest(exactModel[0].Id, null, "Model number", 50000);
            }

            if (k.LabelsByKey.TryGetValue(key, out var uses))
            {
                var candidates = uses.Where(u => group.ManufacturerId == null || k.Items[u.HardwareItemId].ManufacturerId == group.ManufacturerId).ToList();
                var distinctItems = candidates.Select(u => u.HardwareItemId).Distinct().Count();
                if (candidates.Count > 0)
                {
                    var best = candidates.OrderByDescending(u => u.Count).First();
                    if (suggestions.Count == 0 && (group.ManufacturerId != null || distinctItems == 1))
                    {
                        Choose(group, best.HardwareItemId, best.Label, $"Label used on {best.Count} job line(s)", k);
                        return;
                    }
                    Suggest(best.HardwareItemId, best.Label, $"Label used on {best.Count} job line(s)", 40000 + best.Count);
                }
            }
        }

        // 4. Fuzzy suggestions: a past label the callout starts with, then model numbers.
        foreach (var (labelKey, uses) in k.LabelsByKey)
        {
            if (labelKey.Length < 6 || !fullKey.StartsWith(labelKey, StringComparison.Ordinal)) continue;
            foreach (var u in uses)
            {
                if (group.ManufacturerId != null && k.Items[u.HardwareItemId].ManufacturerId != group.ManufacturerId) continue;
                Suggest(u.HardwareItemId, u.Label, "Starts with a label used before", labelKey.Length * 100 + u.Count);
            }
        }
        var firstToken = ImportText.Key(group.Catalog.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
        foreach (var item in pool)
        {
            var model = ImportText.Key(item.ModelNumber);
            if (model.Length < 3) continue;
            int score = 0;
            if (fullKey.StartsWith(model, StringComparison.Ordinal)) score = model.Length * 100;
            else if (shortKey.Length >= 3 && model.StartsWith(shortKey, StringComparison.Ordinal)) score = shortKey.Length * 80;
            else if (firstToken.Length >= 3 && (model.StartsWith(firstToken, StringComparison.Ordinal) || firstToken.StartsWith(model, StringComparison.Ordinal)))
                score = Math.Min(firstToken.Length, model.Length) * 50;
            if (score > 0) Suggest(item.Id, null, "Similar model number", score);
        }

        group.Status = ImportMatchStatus.NeedsReview;
        group.Suggestions.AddRange(suggestions.Values.OrderByDescending(s => s.Score).Take(8));
        if (string.IsNullOrEmpty(group.Reason))
            group.Reason = group.Suggestions.Count > 0 ? "Not sure, please confirm" : "Not found";

        var top = group.Suggestions.FirstOrDefault();
        if (top != null)
        {
            group.HardwareItemId = top.HardwareItemId;
            group.Label = top.Label ?? DefaultLabel(group.Catalog, k.Items[top.HardwareItemId].ModelNumber);
        }
        else
        {
            group.Label = DefaultLabel(group.Catalog, null);
            group.NewModelNumber = ImportText.StripSize(group.Catalog);
            group.NewManufacturerId = group.ManufacturerId;
        }
    }

    private static void Choose(HardwareImportGroup group, int itemId, string label, string reason, HardwareImportKnowledge k)
    {
        group.Status = ImportMatchStatus.Matched;
        group.HardwareItemId = itemId;
        group.ManufacturerId ??= k.Items[itemId].ManufacturerId;
        group.Label = string.IsNullOrWhiteSpace(label) ? DefaultLabel(group.Catalog, k.Items[itemId].ModelNumber) : label;
        group.Reason = reason;
    }

    /// <summary>
    /// The label to use for a callout: the callout with sizes removed, or blank when that is just
    /// the item's model number.
    /// </summary>
    /// <param name="catalog">The callout.</param>
    /// <param name="modelNumber">The chosen item's model number, if any.</param>
    public static string DefaultLabel(string catalog, string? modelNumber)
    {
        var label = ImportText.StripSize(ImportText.Tidy(catalog));
        if (modelNumber != null && ImportText.Key(label) == ImportText.Key(modelNumber)) return string.Empty;
        return label;
    }

    private static int? ResolveMappingItem(Models.PdfImportItemMapping mapping, HardwareImportKnowledge k)
    {
        if (mapping.HardwareItemId is int id && k.Items.ContainsKey(id)) return id;
        if (mapping.TargetManufacturerId is int mfr && !string.IsNullOrWhiteSpace(mapping.TargetModelNumber))
        {
            var key = ImportText.Key(mapping.TargetModelNumber);
            var item = k.Items.Values.FirstOrDefault(i => i.ManufacturerId == mfr && ImportText.Key(i.ModelNumber) == key);
            if (item != null) return item.Id;
        }
        return null;
    }

    /// <summary>Works out the manufacturer for a PDF manufacturer code (null when unsure).</summary>
    /// <param name="group">The callout (code, PDF name, catalog).</param>
    /// <param name="k">What the database knows.</param>
    public static int? ResolveManufacturer(HardwareImportGroup group, HardwareImportKnowledge k)
    {
        var code = group.ManufacturerCode;

        // Learned alias: enough confirmations and clearly ahead of any other manufacturer for this code.
        if (code.Length > 0 && k.Aliases.TryGetValue(code, out var aliases))
        {
            var ordered = aliases.Where(a => k.Manufacturers.ContainsKey(a.ManufacturerId)).OrderByDescending(a => a.ConfirmCount).ToList();
            if (ordered.Count > 0 && ordered[0].ConfirmCount >= k.Threshold &&
                (ordered.Count == 1 || ordered[0].ConfirmCount >= 3 * ordered[1].ConfirmCount))
                return ordered[0].ManufacturerId;
        }

        // The PDF's own code list.
        if (!string.IsNullOrWhiteSpace(group.PdfManufacturerName))
        {
            var byName = FindManufacturerByName(group.PdfManufacturerName!, k);
            if (byName != null) return byName;
        }

        // Common codes ("MK01" → "MK").
        if (code.Length > 0)
        {
            var letters = Regex.Replace(code, @"\d+$", string.Empty);
            if (CommonCodes.TryGetValue(code, out var names) || (letters.Length >= 2 && CommonCodes.TryGetValue(letters, out names)))
            {
                foreach (var name in names)
                {
                    var match = k.Manufacturers.FirstOrDefault(m => string.Equals(m.Value, name, StringComparison.OrdinalIgnoreCase));
                    if (match.Key != 0) return match.Key;
                }
            }
        }

        // A manufacturer named inside the callout ("8080-T-W 3/4" GRI").
        var tokens = new HashSet<string>(Regex.Matches(group.Catalog.ToUpperInvariant(), @"[A-Z0-9]+").Select(m => m.Value));
        var inside = k.Manufacturers.Where(m => m.Value.Length >= 3 && tokens.Contains(m.Value.ToUpperInvariant())).Select(m => m.Key).ToList();
        return inside.Count == 1 ? inside[0] : null;
    }

    private static int? FindManufacturerByName(string pdfName, HardwareImportKnowledge k)
    {
        static List<string> Words(string s) => Regex.Matches(s.ToUpperInvariant(), @"[A-Z0-9]+").Select(m => m.Value).ToList();
        var words = Words(pdfName).Where(w => !NameNoise.Contains(w)).ToList();
        if (words.Count == 0) return null;
        var joined = string.Concat(words);

        var exact = k.Manufacturers.Where(m =>
        {
            var mw = Words(m.Value).Where(w => !NameNoise.Contains(w)).ToList();
            return mw.Count > 0 && (string.Concat(mw) == joined || (mw.Count == 1 && mw[0] == words[0]));
        }).Select(m => m.Key).ToList();
        if (exact.Count == 1) return exact[0];

        // Initials: "National Guard Products" → NGP.
        var initials = string.Concat(Words(pdfName).Where(w => w is not ("INC" or "CO")).Select(w => w[0]));
        var byInitials = k.Manufacturers.Where(m => ImportText.Key(m.Value) == ImportText.Key(initials)).Select(m => m.Key).ToList();
        return byInitials.Count == 1 ? byInitials[0] : null;
    }
}
