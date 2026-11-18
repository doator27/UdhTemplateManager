using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>An entry in the wizard's hardware item list.</summary>
public sealed class ImportItemChoice
{
    /// <summary>Gets or sets the hardware item.</summary>
    public int ItemId { get; init; }

    /// <summary>Gets or sets the suggested label (suggestions only).</summary>
    public string? Label { get; init; }

    /// <summary>Gets or sets whether this is one of the matcher's suggestions.</summary>
    public bool Suggested { get; init; }

    /// <summary>Gets or sets the display text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>One line to add to the Bulk Add draft.</summary>
/// <param name="ManufacturerId">Manufacturer.</param>
/// <param name="DescriptionId">Description.</param>
/// <param name="ModelNumber">Model number.</param>
/// <param name="Label">Label (empty = none).</param>
/// <param name="HardwareItemId">The existing item, or null for a new one.</param>
public sealed record ImportDraftLine(int ManufacturerId, int DescriptionId, string ModelNumber, string Label, int? HardwareItemId);

/// <summary>A line already on the job (active release) or in its Bulk Add draft.</summary>
/// <param name="ItemId">The hardware item, when known.</param>
/// <param name="ManufacturerId">Manufacturer.</param>
/// <param name="ModelNumber">Model number.</param>
/// <param name="Label">Label (empty = none).</param>
/// <param name="Where">"on the job" or "in Bulk Add", for display.</param>
public sealed record ExistingJobLine(int? ItemId, int ManufacturerId, string ModelNumber, string Label, string Where);

/// <summary>
/// The non-UI side of the "Parse PDF into Bulk Add" wizard, shared by both desktop apps: lookups
/// for display, the summary, the list of items to offer for a callout, recording what the user
/// confirmed (<see cref="HardwareImportLearningService"/>), and the final list of draft lines.
/// </summary>
public sealed class HardwareImportSession
{
    private readonly Dictionary<int, ImportItemInfo> _items;
    private readonly Dictionary<int, string> _manufacturers;
    private readonly Dictionary<int, Description> _descriptions;
    private readonly HashSet<(int ItemId, string Label)> _onJob;
    private readonly List<ExistingJobLine> _existing = new();
    private readonly LabelLearningData _labelLearning = new();

    /// <summary>
    /// Loads the lookups for a job (and release) from the database, marks the callouts that are
    /// already on the job or in its Bulk Add draft (they are left out of the wizard), and finds
    /// the similar callouts to ask about.
    /// </summary>
    /// <param name="analysis">The analysed PDF.</param>
    /// <param name="jobId">The job being added to.</param>
    /// <param name="releaseId">The active release (null = base list).</param>
    /// <param name="draftLines">What is already in the release's Bulk Add draft (model number + label per line).</param>
    public HardwareImportSession(HardwarePdfAnalysis analysis, int jobId, int? releaseId, IEnumerable<ImportDraftLine>? draftLines = null)
    {
        Analysis = analysis;
        using var ctx = DatabaseInitializer.CreateContext();
        _items = ctx.HardwareItems.Where(h => h.IsActive)
            .Select(h => new ImportItemInfo(h.Id, h.ManufacturerId, h.DescriptionId, h.ModelNumber))
            .ToList().ToDictionary(i => i.Id);
        _manufacturers = ctx.Manufacturers.ToDictionary(m => m.Id, m => m.ManufacturerName);
        _descriptions = ctx.Descriptions.ToList().ToDictionary(d => d.Id);
        Descriptions = _descriptions.Values.ToList();
        _onJob = ctx.JobHardware
            .Where(jh => jh.JobId == jobId && jh.ReleaseId == releaseId)
            .Select(jh => new { jh.HardwareItemId, jh.CustomDescription })
            .ToList()
            .Select(x => (x.HardwareItemId, NormLabel(x.CustomDescription)))
            .ToHashSet();

        foreach (var (itemId, label) in _onJob)
            if (_items.TryGetValue(itemId, out var item))
                _existing.Add(new ExistingJobLine(itemId, item.ManufacturerId, item.ModelNumber, label, "on the job"));
        foreach (var d in draftLines ?? Enumerable.Empty<ImportDraftLine>())
            _existing.Add(new ExistingJobLine(d.HardwareItemId, d.ManufacturerId, d.ModelNumber, d.Label, "in Bulk Add"));

        MarkAlreadyAdded(Analysis.Groups, _existing, Item);
        Clusters = HardwareImportSimilarity.FindClusters(Analysis.Groups);
        try
        {
            HardwareImportAssemblies.Suggest(Analysis.Groups, HardwareImportAssemblies.Load(ctx));
        }
        catch
        {
            // Suggestions are extra help only.
        }

        // Learned label edits apply to labels the app made up (not to labels used before).
        try
        {
            _labelLearning = HardwareImportLabelLearning.Load(ctx);
        }
        catch
        {
            _labelLearning = new LabelLearningData();
        }
        foreach (var g in Analysis.Groups)
        {
            if (Item(g.HardwareItemId) is { } item && g.Label == HardwareImportMatcher.DefaultLabel(g.Catalog, item.ModelNumber))
                g.Label = LearnedLabel(g.Label, item.ManufacturerId, item.ModelNumber);
            g.OfferedLabel = g.Label;
        }
    }

    /// <summary>Gets the sets of similar callouts to ask about, in PDF order.</summary>
    public List<SimilarCluster> Clusters { get; }

    /// <summary>
    /// Marks callouts already on the job or in Bulk Add (<see cref="HardwareImportGroup.AlreadyAddedReason"/>):
    /// the item and label it resolves to are there, a line's label is this callout, or an
    /// unlabelled line's model number is this callout (same manufacturer when known).
    /// </summary>
    /// <param name="groups">The callouts.</param>
    /// <param name="existing">The lines already on the job (active release) and in Bulk Add.</param>
    /// <param name="item">Looks up an active hardware item.</param>
    public static void MarkAlreadyAdded(IEnumerable<HardwareImportGroup> groups, IReadOnlyList<ExistingJobLine> existing, Func<int?, ImportItemInfo?> item)
    {
        if (existing.Count == 0) return;
        var lines = existing.Select(e => (Line: e, ModelKey: ImportText.Key(e.ModelNumber), Label: NormLabel(e.Label), LabelKey: ImportText.Key(e.Label))).ToList();
        foreach (var g in groups)
        {
            var stripped = ImportText.Key(ImportText.StripNotesAndSize(g.Catalog));
            var mfr = g.ManufacturerId ?? g.NewManufacturerId;
            var chosen = item(g.HardwareItemId);
            var label = NormLabel(g.Label);
            var hit = lines.FirstOrDefault(e =>
                (chosen != null && label == e.Label &&
                 (e.Line.ItemId == chosen.Id || (e.Line.ItemId == null && e.Line.ManufacturerId == chosen.ManufacturerId && e.ModelKey == ImportText.Key(chosen.ModelNumber)))) ||
                ((mfr == null || e.Line.ManufacturerId == mfr) && e.LabelKey.Length > 0 && (e.LabelKey == g.SourceKey || e.LabelKey == stripped)) ||
                ((mfr == null || e.Line.ManufacturerId == mfr) && e.Label.Length == 0 && e.ModelKey.Length > 0 && e.ModelKey == stripped));
            if (hit.Line != null) g.AlreadyAddedReason = $"Already {hit.Line.Where}";
        }
    }

    /// <summary>Gets the analysis.</summary>
    public HardwarePdfAnalysis Analysis { get; }

    /// <summary>Gets all descriptions (for the description picker).</summary>
    public IReadOnlyList<Description> Descriptions { get; }

    /// <summary>Gets the manufacturers sorted by name.</summary>
    public IReadOnlyList<KeyValuePair<int, string>> Manufacturers =>
        _manufacturers.OrderBy(m => m.Value, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Gets a manufacturer's name.</summary>
    /// <param name="id">Manufacturer id.</param>
    public string ManufacturerName(int? id) => id is int i && _manufacturers.TryGetValue(i, out var n) ? n : "?";

    /// <summary>Gets an active hardware item.</summary>
    /// <param name="id">Item id.</param>
    public ImportItemInfo? Item(int? id) => id is int i && _items.TryGetValue(i, out var item) ? item : null;

    /// <summary>Gets a description's full path, e.g. "Closers &gt; Surface Closer".</summary>
    /// <param name="id">Description id.</param>
    public string DescriptionPath(int? id)
    {
        if (id is not int start || !_descriptions.ContainsKey(start)) return string.Empty;
        var parts = new List<string>();
        var seen = new HashSet<int>();
        for (int? cur = start; cur is int c && _descriptions.TryGetValue(c, out var d) && seen.Add(c); cur = d.ParentId)
            parts.Insert(0, d.DescriptionText);
        return string.Join(" > ", parts);
    }

    /// <summary>Gets the groups that need the user.</summary>
    public int ReviewCount => Analysis.Groups.Count(g => g.Status == ImportMatchStatus.NeedsReview && InPlay(g));

    /// <summary>True when the callout is still to be added (not already there, not combined into another, not unticked).</summary>
    /// <param name="g">The callout.</param>
    public static bool InPlay(HardwareImportGroup g) => !g.IsAlreadyAdded && g.MergedInto == null && !g.LeftOutAsSimilar && g.PartOfAssembly == null;

    /// <summary>Summary for the wizard's first page.</summary>
    public string SummaryText
    {
        get
        {
            var a = Analysis;
            var sb = new StringBuilder();
            if (a.Groups.Count == 0)
            {
                sb.Append("No hardware lines were found in this PDF.");
            }
            else
            {
                var format = a.Formats.Count > 0 ? string.Join(" + ", a.Formats).ToLowerInvariant() : "callouts already in the database";
                sb.Append($"Read {a.LineCount} hardware line(s) ({format}): {a.Groups.Count} distinct item(s). ");
                int already = a.Groups.Count(g => g.IsAlreadyAdded);
                if (already > 0) sb.Append($"{already} already on the job or in Bulk Add (skipped), ");
                sb.Append($"{a.Groups.Count(g => g.Status == ImportMatchStatus.Matched && !g.IsAlreadyAdded)} identified automatically, ");
                sb.Append($"{ReviewCount} need you");
                int skipped = a.Groups.Count(g => g.Status == ImportMatchStatus.Skipped);
                if (skipped > 0) sb.Append($", {skipped} skipped (you've skipped them {a.Threshold}+ times)");
                sb.Append('.');
                if (Clusters.Count > 0)
                    sb.Append($"\n{Clusters.Count} set(s) of similar items: you'll be asked which to keep and whether they're one item or separate items.");
            }
            if (a.RecognizedPages.Count > 0)
                sb.Append($"\nScanned page(s) {Pages(a.RecognizedPages)} were read with text recognition: check those items closely.");
            if (a.UnreadablePages.Count > 0)
                sb.Append($"\nScanned page(s) {Pages(a.UnreadablePages)} couldn't be read (text recognition isn't available on this computer).");
            return sb.ToString();
        }
    }

    /// <summary>One display line per group for the summary list.</summary>
    /// <param name="g">The group.</param>
    public string SummaryLine(HardwareImportGroup g)
    {
        var mark = g.IsAlreadyAdded ? "have" : g.Status switch { ImportMatchStatus.Matched => "OK", ImportMatchStatus.Skipped => "skip", _ => "??" };
        string target = g.IsAlreadyAdded ? $"({g.AlreadyAddedReason!.ToLowerInvariant()})"
            : g.MergedInto != null ? $"(same item as {g.MergedInto.Catalog})"
            : g.PartOfAssembly != null ? $"(part of the assembly {g.PartOfAssembly.Catalog})"
            : g.Skip ? "(left out)"
            : g.CreateNew ? $"new: {ManufacturerName(g.NewManufacturerId)} {g.NewModelNumber}"
            : Item(g.HardwareItemId) is { } item ? $"{ManufacturerName(item.ManufacturerId)} {item.ModelNumber}" : "(not identified)";
        var label = !g.Skip && !string.IsNullOrWhiteSpace(g.Label) ? $"  [{g.Label}]" : string.Empty;
        return $"{mark,-4} {g.DisplayText}   ->   {target}{label}";
    }

    /// <summary>The groups to step through: those needing review, plus the automatic ones if asked.</summary>
    /// <param name="includeAutomatic">Also step through matched and learned-skip groups.</param>
    public List<HardwareImportGroup> BuildSteps(bool includeAutomatic) =>
        Analysis.Groups.Where(g => InPlay(g) && (includeAutomatic || g.Status == ImportMatchStatus.NeedsReview)).ToList();

    /// <summary>One line for a callout on a "similar items" page.</summary>
    /// <param name="g">The callout.</param>
    public string ClusterLine(HardwareImportGroup g)
    {
        var sets = g.Lines.Select(l => l.SetName).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        var where = sets.Count > 0 ? $", sets {string.Join(", ", sets.Take(4))}{(sets.Count > 4 ? ", ..." : string.Empty)}" : string.Empty;
        var known = g.Status == ImportMatchStatus.Matched && Item(g.HardwareItemId) is { } item
            ? $"   -> {ManufacturerName(item.ManufacturerId)} {item.ModelNumber}{(string.IsNullOrWhiteSpace(g.Label) ? string.Empty : $" [{g.Label}]")}"
            : string.Empty;
        return $"{g.GenericName}  {g.Catalog}   (qty {g.Lines.Sum(l => l.Quantity)}{where}){known}";
    }

    /// <summary>Multi-line description of what the PDF says about a callout.</summary>
    /// <param name="g">The group.</param>
    public string SourceText(HardwareImportGroup g)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Manufacturer code: {(g.ManufacturerCode.Length > 0 ? g.ManufacturerCode : "(none)")}{(g.PdfManufacturerName != null ? $"  ({g.PdfManufacturerName})" : string.Empty)}");
        sb.AppendLine($"Item: {g.GenericName}");
        sb.AppendLine($"Callout: {g.Catalog}");
        var finishes = g.Lines.Select(l => l.Finish).Where(f => !string.IsNullOrEmpty(f)).Distinct().ToList();
        var handings = g.Lines.Select(l => l.Handing).Where(h => !string.IsNullOrEmpty(h)).Distinct().ToList();
        if (finishes.Count > 0 || handings.Count > 0)
            sb.AppendLine($"Finish: {string.Join(", ", finishes)}   Handing: {string.Join(", ", handings)}");
        if (g.IsAssembly)
            sb.AppendLine($"Assembly with: {string.Join(" | ", g.AssemblyParts.Select(p => p.Catalog))}");
        if (g.MergedGroups.Count > 0)
            sb.AppendLine($"Also covers (same item): {string.Join("; ", g.MergedGroups.Select(m => m.Catalog))}");
        var sets = g.Sets;
        sb.Append($"Qty {g.TotalQuantity} on {g.AllLines.Count()} line(s)");
        if (sets.Count > 0) sb.Append($", sets {string.Join(", ", sets.Take(6))}{(sets.Count > 6 ? ", ..." : string.Empty)}");
        sb.Append($", page(s) {Pages(g.AllLines.Select(l => l.Page))}");
        return sb.ToString();
    }

    /// <summary>
    /// The items to offer for a callout: the matcher's suggestions first, then the chosen
    /// manufacturer's items (filtered by <paramref name="search"/>), or a search across all
    /// manufacturers when none is chosen.
    /// </summary>
    /// <param name="g">The group.</param>
    /// <param name="manufacturerId">The manufacturer picked in the wizard.</param>
    /// <param name="search">Model number search text.</param>
    public List<ImportItemChoice> Choices(HardwareImportGroup g, int? manufacturerId, string? search)
    {
        var list = new List<ImportItemChoice>();
        var seen = new HashSet<(int, string?)>();
        foreach (var s in g.Suggestions)
        {
            var item = Item(s.HardwareItemId);
            if (item == null || !seen.Add((s.HardwareItemId, s.Label))) continue;
            var label = s.Label != null ? $"  [{s.Label}]" : string.Empty;
            list.Add(new ImportItemChoice
            {
                ItemId = item.Id,
                Label = s.Label,
                Suggested = true,
                Text = $"* {ManufacturerName(item.ManufacturerId)}  {item.ModelNumber}{label}  -  {DescriptionPath(item.DescriptionId)}   ({s.Reason})",
            });
        }

        var term = (search ?? string.Empty).Trim();
        var termKey = ImportText.Key(term);
        IEnumerable<ImportItemInfo> pool = manufacturerId is int mfr
            ? _items.Values.Where(i => i.ManufacturerId == mfr)
            : termKey.Length >= 2 ? _items.Values : Enumerable.Empty<ImportItemInfo>();
        if (termKey.Length > 0)
            pool = pool.Where(i => ImportText.Key(i.ModelNumber).Contains(termKey, StringComparison.Ordinal) ||
                                   i.ModelNumber.Contains(term, StringComparison.OrdinalIgnoreCase));
        foreach (var item in pool.OrderBy(i => i.ModelNumber, StringComparer.OrdinalIgnoreCase).Take(300))
        {
            if (!seen.Add((item.Id, null))) continue;
            list.Add(new ImportItemChoice
            {
                ItemId = item.Id,
                Text = $"{(manufacturerId == null ? ManufacturerName(item.ManufacturerId) + "  " : string.Empty)}{item.ModelNumber}  -  {DescriptionPath(item.DescriptionId)}",
            });
        }
        return list;
    }

    /// <summary>The label to fill in when a choice is picked.</summary>
    /// <param name="g">The group.</param>
    /// <param name="choice">The picked item.</param>
    public string LabelFor(HardwareImportGroup g, ImportItemChoice choice)
    {
        var item = Item(choice.ItemId);
        var label = choice.Label ?? LearnedLabel(HardwareImportMatcher.DefaultLabel(g.Catalog, item?.ModelNumber), item?.ManufacturerId, item?.ModelNumber);
        g.OfferedLabel = label;
        return label;
    }

    /// <summary>A label with what was learned from the user's edits applied (words removed, text added).</summary>
    /// <param name="label">The label.</param>
    /// <param name="manufacturerId">The item's manufacturer.</param>
    /// <param name="modelNumber">The item's model number.</param>
    public string LearnedLabel(string label, int? manufacturerId, string? modelNumber) =>
        HardwareImportLabelLearning.Apply(_labelLearning, label, HardwareImportLabelLearning.ItemKey(manufacturerId, modelNumber), Analysis.Threshold);

    /// <summary>
    /// The label choices for an item that covers several labels: one per callout it covers (this
    /// callout and the look-alikes combined into it), plus labels used with the chosen item before
    /// (unticked). Edited texts and ticks in <paramref name="previous"/> are kept. Fewer than two
    /// distinct labels = an empty list (the single label box is used).
    /// </summary>
    /// <param name="g">The callout.</param>
    /// <param name="itemId">The chosen existing item (null for a new item).</param>
    /// <param name="manufacturerId">The item's manufacturer.</param>
    /// <param name="modelNumber">The item's model number.</param>
    /// <param name="previous">The current choices, if any.</param>
    /// <param name="chosenLabel">The label picked with the item in the list (used for this callout's own row).</param>
    public List<ImportLabelChoice> BuildLabelChoices(HardwareImportGroup g, int? itemId, int? manufacturerId, string? modelNumber, IReadOnlyList<ImportLabelChoice>? previous, string? chosenLabel = null)
    {
        var rows = new List<ImportLabelChoice>();
        var seen = new HashSet<string>();
        if (g.IsAssembly) return rows;

        foreach (var src in new[] { g }.Concat(g.MergedGroups))
        {
            var offered = src == g && chosenLabel != null
                ? chosenLabel
                : LearnedLabel(HardwareImportMatcher.DefaultLabel(src.Catalog, modelNumber), manufacturerId, modelNumber);
            var old = previous?.FirstOrDefault(r => r.Source == src);
            bool edited = old != null && old.Text != old.DefaultText;
            var row = new ImportLabelChoice
            {
                Source = src,
                DefaultText = offered,
                Text = edited ? old!.Text : offered,
                Include = old?.Include ?? true,
                Origin = $"{src.Catalog}  (qty {src.Lines.Sum(l => l.Quantity)})",
            };
            if (seen.Add(NormLabel(row.Text))) rows.Add(row);
        }
        foreach (var s in g.Suggestions.Where(s => s.HardwareItemId == itemId && !string.IsNullOrWhiteSpace(s.Label)))
        {
            if (!seen.Add(NormLabel(s.Label))) continue;
            var old = previous?.FirstOrDefault(r => r.Source == null && r.DefaultText == s.Label);
            rows.Add(new ImportLabelChoice
            {
                DefaultText = s.Label!,
                Text = old?.Text ?? s.Label!,
                Include = old?.Include ?? false,
                Origin = "used with this item before",
            });
        }
        return rows.Count >= 2 ? rows : new List<ImportLabelChoice>();
    }

    /// <summary>Records the user's decisions for the groups they stepped through.</summary>
    /// <param name="reviewed">The reviewed groups.</param>
    public void RecordLearning(IEnumerable<HardwareImportGroup> reviewed)
    {
        // Callouts combined into a reviewed one learn the same answer; unticked similar callouts
        // are left out for this job only, so they teach nothing.
        HardwareImportSimilarity.CopyCombinedAnswers(Analysis.Groups);
        var list = reviewed.Where(g => g.PartOfAssembly == null).ToList();
        ObserveLabelEdits(list);
        list.AddRange(list.SelectMany(g => g.MergedGroups).Where(m => !list.Contains(m)).ToList());
        using var ctx = DatabaseInitializer.CreateContext();
        HardwareImportLearningService.Record(ctx, list.Where(g => !g.LeftOutAsSimilar),
            id => Item(id) is { } i ? (i.ManufacturerId, i.ModelNumber) : null);
        try
        {
            HardwareImportLabelLearning.Save(ctx, _labelLearning);
        }
        catch
        {
            // Label learning is extra help only.
        }
        // Assemblies made this time are offered again when the same callouts come up.
        var assemblies = Analysis.Groups.Where(g => g.IsAssembly && !g.Skip && InPlay(g)).ToList();
        if (assemblies.Count > 0)
        {
            var learned = HardwareImportAssemblies.Load(ctx);
            HardwareImportAssemblies.Record(learned, assemblies);
            HardwareImportAssemblies.Save(ctx, learned);
        }
    }

    /// <summary>Feeds the user's label edits on the reviewed callouts to the label learning.</summary>
    private void ObserveLabelEdits(IEnumerable<HardwareImportGroup> reviewed)
    {
        foreach (var g in reviewed)
        {
            if (g.Skip || g.IsAssembly) continue;
            int? mfr = g.CreateNew ? g.NewManufacturerId : Item(g.HardwareItemId)?.ManufacturerId;
            string? model = g.CreateNew ? g.NewModelNumber : Item(g.HardwareItemId)?.ModelNumber;
            if (mfr == null || string.IsNullOrWhiteSpace(model)) continue;
            var key = HardwareImportLabelLearning.ItemKey(mfr, model);
            if (g.LabelChoices.Count > 0)
            {
                foreach (var row in g.LabelChoices.Where(r => r.Include && r.Source != null))
                    HardwareImportLabelLearning.Observe(_labelLearning, row.DefaultText, row.Text, key);
            }
            else if (!string.IsNullOrEmpty(g.OfferedLabel) || !string.IsNullOrEmpty(g.Label))
            {
                HardwareImportLabelLearning.Observe(_labelLearning, g.OfferedLabel, g.Label, key);
            }
        }
    }

    /// <summary>
    /// The lines to add to the Bulk Add draft, in PDF order, one per distinct item + label;
    /// skipped groups and groups with nothing chosen are left out.
    /// </summary>
    public List<ImportDraftLine> DraftLines()
    {
        var result = new List<ImportDraftLine>();
        var seen = new HashSet<(int, int, string, string)>();
        foreach (var g in Analysis.Groups)
        {
            if (g.Skip || !InPlay(g)) continue;
            ImportDraftLine? line = null;
            if (g.CreateNew && g.NewManufacturerId is int mfr && g.NewDescriptionId is int desc && !string.IsNullOrWhiteSpace(g.NewModelNumber))
                line = new ImportDraftLine(mfr, desc, g.NewModelNumber.Trim(), g.Label.Trim(), null);
            else if (!g.CreateNew && Item(g.HardwareItemId) is { } item)
                line = new ImportDraftLine(item.ManufacturerId, item.DescriptionId, item.ModelNumber, g.Label.Trim(), item.Id);
            if (line == null) continue;
            // An item covering several labels: one line per ticked label.
            var labels = g.LabelChoices.Count > 0
                ? g.LabelChoices.Where(r => r.Include).Select(r => r.Text.Trim()).ToList()
                : new List<string> { line.Label };
            foreach (var label in labels)
            {
                var one = line with { Label = label };
                if (seen.Add((one.ManufacturerId, one.DescriptionId, one.ModelNumber.ToUpperInvariant(), NormLabel(one.Label))))
                    result.Add(one);
            }
        }
        return result;
    }

    /// <summary>True when this item + label is already on the job (active release).</summary>
    /// <param name="line">The draft line.</param>
    public bool IsOnJob(ImportDraftLine line) =>
        line.HardwareItemId is int id && _onJob.Contains((id, NormLabel(line.Label)));

    /// <summary>Label comparison form: trimmed, upper case, null = empty.</summary>
    /// <param name="label">A label.</param>
    public static string NormLabel(string? label) => (label ?? string.Empty).Trim().ToUpperInvariant();

    private static string Pages(IEnumerable<int> pages)
    {
        var list = pages.Distinct().OrderBy(p => p).ToList();
        return list.Count <= 8 ? string.Join(", ", list) : $"{string.Join(", ", list.Take(8))}, ...";
    }
}
