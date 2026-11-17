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

    /// <summary>Loads the lookups for a job (and release) from the database.</summary>
    /// <param name="analysis">The analysed PDF.</param>
    /// <param name="jobId">The job being added to.</param>
    /// <param name="releaseId">The active release (null = base list).</param>
    public HardwareImportSession(HardwarePdfAnalysis analysis, int jobId, int? releaseId)
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
    public int ReviewCount => Analysis.Groups.Count(g => g.Status == ImportMatchStatus.NeedsReview);

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
                sb.Append($"{a.Groups.Count(g => g.Status == ImportMatchStatus.Matched)} identified automatically, ");
                sb.Append($"{ReviewCount} need you");
                int skipped = a.Groups.Count(g => g.Status == ImportMatchStatus.Skipped);
                if (skipped > 0) sb.Append($", {skipped} skipped (you've skipped them {a.Threshold}+ times)");
                sb.Append('.');
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
        var mark = g.Status switch { ImportMatchStatus.Matched => "OK", ImportMatchStatus.Skipped => "skip", _ => "??" };
        string target = g.Skip ? "(left out)"
            : g.CreateNew ? $"new: {ManufacturerName(g.NewManufacturerId)} {g.NewModelNumber}"
            : Item(g.HardwareItemId) is { } item ? $"{ManufacturerName(item.ManufacturerId)} {item.ModelNumber}" : "(not identified)";
        var label = !g.Skip && !string.IsNullOrWhiteSpace(g.Label) ? $"  [{g.Label}]" : string.Empty;
        return $"{mark,-4} {g.DisplayText}   ->   {target}{label}";
    }

    /// <summary>The groups to step through: those needing review, plus the automatic ones if asked.</summary>
    /// <param name="includeAutomatic">Also step through matched and learned-skip groups.</param>
    public List<HardwareImportGroup> BuildSteps(bool includeAutomatic) =>
        Analysis.Groups.Where(g => includeAutomatic || g.Status == ImportMatchStatus.NeedsReview).ToList();

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
        var sets = g.Sets;
        sb.Append($"Qty {g.TotalQuantity} on {g.Lines.Count} line(s)");
        if (sets.Count > 0) sb.Append($", sets {string.Join(", ", sets.Take(6))}{(sets.Count > 6 ? ", ..." : string.Empty)}");
        sb.Append($", page(s) {Pages(g.Lines.Select(l => l.Page))}");
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
    public string LabelFor(HardwareImportGroup g, ImportItemChoice choice) =>
        choice.Label ?? HardwareImportMatcher.DefaultLabel(g.Catalog, Item(choice.ItemId)?.ModelNumber);

    /// <summary>Records the user's decisions for the groups they stepped through.</summary>
    /// <param name="reviewed">The reviewed groups.</param>
    public void RecordLearning(IEnumerable<HardwareImportGroup> reviewed)
    {
        using var ctx = DatabaseInitializer.CreateContext();
        HardwareImportLearningService.Record(ctx, reviewed, id => Item(id) is { } i ? (i.ManufacturerId, i.ModelNumber) : null);
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
            if (g.Skip) continue;
            ImportDraftLine? line = null;
            if (g.CreateNew && g.NewManufacturerId is int mfr && g.NewDescriptionId is int desc && !string.IsNullOrWhiteSpace(g.NewModelNumber))
                line = new ImportDraftLine(mfr, desc, g.NewModelNumber.Trim(), g.Label.Trim(), null);
            else if (!g.CreateNew && Item(g.HardwareItemId) is { } item)
                line = new ImportDraftLine(item.ManufacturerId, item.DescriptionId, item.ModelNumber, g.Label.Trim(), item.Id);
            if (line == null) continue;
            if (seen.Add((line.ManufacturerId, line.DescriptionId, line.ModelNumber.ToUpperInvariant(), NormLabel(line.Label))))
                result.Add(line);
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
