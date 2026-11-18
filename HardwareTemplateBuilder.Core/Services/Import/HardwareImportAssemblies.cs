using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>One part of a remembered assembly: a callout's manufacturer code and comparison key.</summary>
public sealed class AssemblyPartKey
{
    /// <summary>Gets or sets the manufacturer code as printed in the PDF.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the callout's comparison key (<see cref="ImportText.Key"/>).</summary>
    public string Key { get; set; } = string.Empty;
}

/// <summary>An assembly the user made in Parse PDF into Bulk Add, remembered to suggest it again.</summary>
public sealed class LearnedAssembly
{
    /// <summary>Gets or sets the assembly line's callout (code + key).</summary>
    public AssemblyPartKey Anchor { get; set; } = new();

    /// <summary>Gets or sets the parts that went with it.</summary>
    public List<AssemblyPartKey> Parts { get; set; } = new();

    /// <summary>Gets or sets the description that started its label.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the description (category) it was given, if any.</summary>
    public int? DescriptionId { get; set; }

    /// <summary>Gets or sets how many times it was made.</summary>
    public int Count { get; set; }

    /// <summary>Gets or sets when it was last made (UTC).</summary>
    public DateTime LastUsedUtc { get; set; }
}

/// <summary>
/// Assemblies in "Parse PDF into Bulk Add": a callout marked as an assembly takes other callouts
/// from the same heading/set as its parts. The assembly is one line whose label is the chosen
/// description followed by every callout, joined with " | "; the parts are left out as lines of
/// their own. Assemblies the user makes are remembered (App Setting <see cref="SettingKey"/>) and
/// suggested when the same callouts come up again; the user always confirms.
/// </summary>
public static class HardwareImportAssemblies
{
    /// <summary>The <c>AppSetting</c> key the remembered assemblies are stored under (JSON).</summary>
    public const string SettingKey = "PdfImportAssemblies";

    /// <summary>The most assemblies remembered (least recently used are dropped).</summary>
    public const int MaxRemembered = 500;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>
    /// The callouts that can be parts of <paramref name="anchor"/>'s assembly: those in the same
    /// heading/set (any shared set or opening), or every callout when <paramref name="allHeadings"/>
    /// is set or the PDF has no sets. Callouts already on the job, combined into another, left
    /// out, or in another assembly (or assemblies themselves) are not offered. PDF order.
    /// </summary>
    /// <param name="anchor">The assembly's callout.</param>
    /// <param name="groups">All callouts.</param>
    /// <param name="allHeadings">Offer callouts from every heading.</param>
    public static List<HardwareImportGroup> Candidates(HardwareImportGroup anchor, IEnumerable<HardwareImportGroup> groups, bool allHeadings)
    {
        var mine = SetTokens(anchor);
        return groups.Where(g =>
                g != anchor && !g.IsAlreadyAdded && g.MergedInto == null && !g.LeftOutAsSimilar && !g.IsAssembly &&
                (g.PartOfAssembly == null || g.PartOfAssembly == anchor) &&
                (allHeadings || mine.Count == 0 || SetTokens(g).Overlaps(mine)))
            .ToList();
    }

    /// <summary>
    /// The PDF pages the callout's heading/set is on: every page with a line from the same
    /// heading (any callout), else the callout's own pages. Used to preview the heading in full.
    /// </summary>
    /// <param name="anchor">The callout.</param>
    /// <param name="groups">All callouts.</param>
    public static List<int> HeadingPages(HardwareImportGroup anchor, IEnumerable<HardwareImportGroup> groups)
    {
        var sets = new HashSet<string>(anchor.Sets, StringComparer.OrdinalIgnoreCase);
        var pages = sets.Count == 0
            ? new List<int>()
            : groups.SelectMany(g => g.AllLines)
                .Where(l => l.SetName != null && sets.Contains(l.SetName) && l.Page > 0)
                .Select(l => l.Page).ToList();
        if (pages.Count == 0) pages = anchor.AllLines.Where(l => l.Page > 0).Select(l => l.Page).ToList();
        return pages.Distinct().OrderBy(p => p).ToList();
    }

    /// <summary>The assembly's label: the description (when given), then each callout, joined with " | ".</summary>
    /// <param name="description">The description to start with (blank = none).</param>
    /// <param name="anchor">The assembly's callout.</param>
    /// <param name="parts">Its parts, in order.</param>
    public static string Label(string? description, HardwareImportGroup anchor, IEnumerable<HardwareImportGroup> parts)
    {
        var pieces = new List<string>();
        if (!string.IsNullOrWhiteSpace(description)) pieces.Add(description.Trim());
        pieces.Add(ImportText.Tidy(anchor.Catalog));
        pieces.AddRange(parts.Select(p => ImportText.Tidy(p.Catalog)));
        return string.Join(" | ", pieces.Where(p => p.Length > 0));
    }

    /// <summary>
    /// Makes <paramref name="anchor"/> an assembly of <paramref name="parts"/> (replacing any
    /// earlier parts) and sets its label; with no parts, it stops being an assembly (its label
    /// goes back to its own callout when it was the assembly's).
    /// </summary>
    /// <param name="anchor">The assembly's callout.</param>
    /// <param name="parts">The parts.</param>
    /// <param name="description">The description that starts the label.</param>
    /// <param name="descriptionId">The description (category) it comes from, if any.</param>
    public static void Apply(HardwareImportGroup anchor, IEnumerable<HardwareImportGroup> parts, string? description, int? descriptionId = null)
    {
        string oldLabel = anchor.IsAssembly ? Label(anchor.AssemblyDescription, anchor, anchor.AssemblyParts) : string.Empty;
        foreach (var p in anchor.AssemblyParts) p.PartOfAssembly = null;
        anchor.AssemblyParts.Clear();

        var list = parts.Where(p => p != anchor).Distinct().ToList();
        foreach (var p in list)
        {
            p.PartOfAssembly = anchor;
            anchor.AssemblyParts.Add(p);
        }
        anchor.AssemblyDescription = (description ?? string.Empty).Trim();
        anchor.AssemblyDescriptionId = anchor.IsAssembly ? descriptionId : null;
        anchor.LabelChoices.Clear();
        if (anchor.IsAssembly)
            anchor.Label = Label(anchor.AssemblyDescription, anchor, anchor.AssemblyParts);
        else if (oldLabel.Length > 0 && string.Equals(anchor.Label, oldLabel, StringComparison.Ordinal))
            anchor.Label = HardwareImportMatcher.DefaultLabel(anchor.Catalog, null);
    }

    /// <summary>
    /// Marks remembered assemblies: for each callout that was an assembly before, the parts of
    /// that assembly found in this PDF (in the same heading where there are sets) become its
    /// suggestion. Nothing is applied.
    /// </summary>
    /// <param name="groups">All callouts.</param>
    /// <param name="learned">The remembered assemblies.</param>
    public static void Suggest(IReadOnlyList<HardwareImportGroup> groups, IEnumerable<LearnedAssembly> learned)
    {
        var byKey = groups.GroupBy(g => (ImportText.CleanManufacturerCode(g.ManufacturerCode), g.SourceKey))
            .ToDictionary(x => x.Key, x => x.First());
        foreach (var a in learned.OrderByDescending(a => a.Count).ThenByDescending(a => a.LastUsedUtc))
        {
            if (!byKey.TryGetValue((ImportText.CleanManufacturerCode(a.Anchor.Code), a.Anchor.Key), out var anchor)) continue;
            if (anchor.SuggestedAssemblyParts.Count > 0) continue;
            var candidates = Candidates(anchor, groups, allHeadings: false);
            foreach (var part in a.Parts)
            {
                var hit = candidates.FirstOrDefault(c => c.SourceKey == part.Key &&
                    string.Equals(ImportText.CleanManufacturerCode(c.ManufacturerCode), ImportText.CleanManufacturerCode(part.Code), StringComparison.OrdinalIgnoreCase));
                if (hit != null && !anchor.SuggestedAssemblyParts.Contains(hit)) anchor.SuggestedAssemblyParts.Add(hit);
            }
            if (anchor.SuggestedAssemblyParts.Count > 0)
            {
                anchor.SuggestedAssemblyDescription = a.Description;
                anchor.SuggestedAssemblyDescriptionId = a.DescriptionId;
            }
        }
    }

    /// <summary>Adds (or counts again) the assemblies made in this import.</summary>
    /// <param name="learned">The remembered list, updated in place.</param>
    /// <param name="assemblies">The callouts that are assemblies.</param>
    public static void Record(List<LearnedAssembly> learned, IEnumerable<HardwareImportGroup> assemblies)
    {
        foreach (var g in assemblies.Where(g => g.IsAssembly))
        {
            var anchor = new AssemblyPartKey { Code = g.ManufacturerCode, Key = g.SourceKey };
            var parts = g.AssemblyParts.Select(p => new AssemblyPartKey { Code = p.ManufacturerCode, Key = p.SourceKey }).ToList();
            var existing = learned.FirstOrDefault(a => a.Anchor.Key == anchor.Key &&
                string.Equals(a.Anchor.Code, anchor.Code, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new LearnedAssembly { Anchor = anchor };
                learned.Add(existing);
            }
            existing.Parts = parts;
            existing.Description = g.AssemblyDescription;
            existing.DescriptionId = g.AssemblyDescriptionId;
            existing.Count++;
            existing.LastUsedUtc = DateTime.UtcNow;
        }
        if (learned.Count > MaxRemembered)
        {
            var keep = learned.OrderByDescending(a => a.LastUsedUtc).Take(MaxRemembered).ToList();
            learned.Clear();
            learned.AddRange(keep);
        }
    }

    /// <summary>Loads the remembered assemblies (empty when none or unreadable).</summary>
    /// <param name="ctx">An open context.</param>
    public static List<LearnedAssembly> Load(AppDbContext ctx)
    {
        var raw = new AppSettingRepository(ctx).GetValue(SettingKey);
        if (string.IsNullOrWhiteSpace(raw)) return new List<LearnedAssembly>();
        try
        {
            return JsonSerializer.Deserialize<List<LearnedAssembly>>(raw, Json) ?? new List<LearnedAssembly>();
        }
        catch (JsonException)
        {
            return new List<LearnedAssembly>();
        }
    }

    /// <summary>Saves the remembered assemblies.</summary>
    /// <param name="ctx">An open context.</param>
    /// <param name="learned">The list.</param>
    public static void Save(AppDbContext ctx, List<LearnedAssembly> learned) =>
        new AppSettingRepository(ctx).SetValue(SettingKey, JsonSerializer.Serialize(learned, Json));

    /// <summary>
    /// A callout's sets/openings as separate tokens ("Openings: 1(9), 2C(3)" → "1", "2C"), so
    /// callouts in the same heading or on the same openings are recognised.
    /// </summary>
    /// <param name="g">The callout.</param>
    public static HashSet<string> SetTokens(HardwareImportGroup g)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in g.AllLines.Select(l => l.SetName).Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var text = set!;
            int colon = text.IndexOf(':');
            if (colon >= 0 && colon < text.Length - 1 && text.IndexOf(',') > colon)
            {
                // "Type NL1 (SNAP): E100C, U100C" → keep the whole set too, then the marks.
                tokens.Add(text.Trim());
                text = text[(colon + 1)..];
            }
            var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length <= 1)
            {
                tokens.Add(set!.Trim());
                continue;
            }
            foreach (var part in parts)
                tokens.Add(Regex.Replace(part, @"\(\d+\)$", string.Empty).Trim());
        }
        return tokens;
    }
}
