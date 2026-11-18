using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services.TemplateAudit;

/// <summary>Where a template stands in the description audit.</summary>
public enum TemplateAuditStatus
{
    /// <summary>Not looked at yet.</summary>
    NotReviewed = 0,

    /// <summary>Its description is right.</summary>
    Ok = 1,

    /// <summary>Its description needs changing.</summary>
    NeedsChange = 2,
}

/// <summary>The audit mark on one template (stored in App Setting <see cref="TemplateAuditService.SettingKey"/>).</summary>
public sealed class TemplateAuditMark
{
    /// <summary>Gets or sets the status.</summary>
    public TemplateAuditStatus Status { get; set; }

    /// <summary>Gets or sets a note (what to change it to, etc.).</summary>
    public string? Note { get; set; }

    /// <summary>Gets or sets when it was marked (UTC).</summary>
    public DateTime MarkedAt { get; set; }

    /// <summary>Gets or sets who marked it.</summary>
    public string? MarkedBy { get; set; }
}

/// <summary>A hardware item linked to a template.</summary>
/// <param name="ItemId">The hardware item.</param>
/// <param name="ModelNumber">Its model number.</param>
/// <param name="DescriptionId">Its description.</param>
/// <param name="DescriptionPath">Its description path.</param>
/// <param name="JobOnly">True when the link is only for one job.</param>
public sealed record TemplateAuditItem(int ItemId, string ModelNumber, int DescriptionId, string DescriptionPath, bool JobOnly);

/// <summary>One template in the audit.</summary>
public sealed class TemplateAuditRow
{
    /// <summary>Gets the template id.</summary>
    public int TemplateId { get; init; }

    /// <summary>Gets the manufacturer.</summary>
    public int ManufacturerId { get; init; }

    /// <summary>Gets the manufacturer name.</summary>
    public string ManufacturerName { get; init; } = string.Empty;

    /// <summary>Gets the template number.</summary>
    public string TemplateNumber { get; init; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public int DescriptionId { get; set; }

    /// <summary>Gets or sets the description path ("Exit Devices > Rim").</summary>
    public string DescriptionPath { get; set; } = string.Empty;

    /// <summary>Gets the door material.</summary>
    public string DoorMaterial { get; init; } = string.Empty;

    /// <summary>Gets the pages to print.</summary>
    public string PagesToPrint { get; init; } = string.Empty;

    /// <summary>Gets the online link.</summary>
    public string? OnlineLink { get; init; }

    /// <summary>Gets the local file.</summary>
    public string? LocalLink { get; init; }

    /// <summary>Gets the job it is only visible to (null = every job).</summary>
    public int? OriginJobId { get; init; }

    /// <summary>Gets the linked hardware items.</summary>
    public List<TemplateAuditItem> Items { get; init; } = new();

    /// <summary>Gets or sets the audit mark (null = not reviewed).</summary>
    public TemplateAuditMark? Mark { get; set; }

    /// <summary>Gets the status (not reviewed when unmarked).</summary>
    public TemplateAuditStatus Status => Mark?.Status ?? TemplateAuditStatus.NotReviewed;

    /// <summary>Gets whether a linked item has a different description than the template.</summary>
    public bool ItemDescriptionsDiffer => Items.Any(i => i.DescriptionId != DescriptionId);

    /// <summary>The list text: "[!] 1234  (3 items)".</summary>
    public string ListText
    {
        get
        {
            var flag = Status switch
            {
                TemplateAuditStatus.Ok => "[OK]",
                TemplateAuditStatus.NeedsChange => "[CHANGE]",
                _ => "[  ]",
            };
            var items = Items.Count == 0 ? "no items" : Items.Count == 1 ? "1 item" : $"{Items.Count} items";
            var differ = ItemDescriptionsDiffer ? "  · items differ" : string.Empty;
            return $"{flag}  {ManufacturerName}  {TemplateNumber}   —  {DescriptionPath}   ({items}){differ}";
        }
    }

    /// <inheritdoc/>
    public override string ToString() => ListText;
}

/// <summary>A description with how many of the shown templates are in it (including its children).</summary>
/// <param name="DescriptionId">The description (null = every description).</param>
/// <param name="Path">Its path.</param>
/// <param name="Depth">How deep it is in the tree (0 = top level).</param>
/// <param name="Count">Templates in it or under it.</param>
/// <param name="DirectCount">Templates in it exactly.</param>
public sealed record TemplateAuditDescription(int? DescriptionId, string Path, int Depth, int Count, int DirectCount)
{
    /// <inheritdoc/>
    public override string ToString() =>
        DescriptionId == null
            ? $"(All descriptions)  ({Count})"
            : $"{new string(' ', Depth * 4)}{Path.Split(" > ").Last()}  ({Count}{(DirectCount != Count ? $", {DirectCount} here" : string.Empty)})";
}

/// <summary>
/// The template description audit (Maintenance → Template Description Audit): every template with
/// its manufacturer, description, linked hardware items and an audit mark (Not reviewed / OK /
/// Needs change, with a note), browsable by manufacturer and description so descriptions can be
/// checked and reorganized (e.g. exit devices). Marks are stored as JSON in App Setting
/// <see cref="SettingKey"/>; changing a description updates the templates (and optionally their
/// linked hardware items).
/// </summary>
public static class TemplateAuditService
{
    /// <summary>The <c>AppSetting</c> key the audit marks are stored under.</summary>
    public const string SettingKey = "TemplateDescriptionAudit";

    /// <summary>Separator in description paths.</summary>
    public const string PathSeparator = " > ";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>A description's path, root first ("Exit Devices > Rim").</summary>
    /// <param name="id">The description.</param>
    /// <param name="byId">Every description by id.</param>
    public static string Path(int id, IReadOnlyDictionary<int, Description> byId)
    {
        var parts = new List<string>();
        var seen = new HashSet<int>();
        int? cur = id;
        while (cur is int c && byId.TryGetValue(c, out var d) && seen.Add(c))
        {
            parts.Add(d.DescriptionText);
            cur = d.ParentId;
        }
        parts.Reverse();
        return parts.Count == 0 ? "(none)" : string.Join(PathSeparator, parts);
    }

    /// <summary>Loads every template with its links and audit mark.</summary>
    /// <param name="ctx">An open context.</param>
    public static List<TemplateAuditRow> Load(AppDbContext ctx)
    {
        var byId = ctx.Descriptions.AsNoTracking().ToList().ToDictionary(d => d.Id);
        var marks = LoadMarks(ctx);
        var mfrs = ctx.Manufacturers.AsNoTracking().ToDictionary(m => m.Id, m => m.ManufacturerName);
        var materials = ctx.DoorMaterials.AsNoTracking().ToList().ToDictionary(m => m.Id, m => m.Material);
        var links = ctx.HardwareItemTemplates.AsNoTracking()
            .Select(l => new { l.IndividualTemplateId, l.HardwareItemId, l.JobId, l.HardwareItem.ModelNumber, l.HardwareItem.DescriptionId })
            .ToList()
            .GroupBy(l => l.IndividualTemplateId)
            .ToDictionary(g => g.Key, g => g
                .GroupBy(l => l.HardwareItemId)
                .Select(x => x.First() with { JobId = x.All(l => l.JobId != null) ? x.First().JobId : null })
                .Select(l => new TemplateAuditItem(l.HardwareItemId, l.ModelNumber, l.DescriptionId, Path(l.DescriptionId, byId), l.JobId != null))
                .OrderBy(i => i.ModelNumber, StringComparer.OrdinalIgnoreCase)
                .ToList());

        return ctx.IndividualTemplates.AsNoTracking().ToList()
            .Select(t => new TemplateAuditRow
            {
                TemplateId = t.Id,
                ManufacturerId = t.ManufacturerId,
                ManufacturerName = mfrs.GetValueOrDefault(t.ManufacturerId, "?"),
                TemplateNumber = t.TemplateNumber,
                DescriptionId = t.DescriptionId,
                DescriptionPath = Path(t.DescriptionId, byId),
                DoorMaterial = materials.GetValueOrDefault(t.DoorMaterialId, string.Empty),
                PagesToPrint = t.PagesToPrint,
                OnlineLink = t.OnlineLink,
                LocalLink = t.LocalLink,
                OriginJobId = t.OriginJobId,
                Items = links.GetValueOrDefault(t.Id) ?? new List<TemplateAuditItem>(),
                Mark = marks.GetValueOrDefault(t.Id),
            })
            .OrderBy(r => r.ManufacturerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.DescriptionPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.TemplateNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The ids of a description and everything under it.</summary>
    /// <param name="descriptionId">The description.</param>
    /// <param name="all">Every description.</param>
    public static HashSet<int> Subtree(int descriptionId, IEnumerable<Description> all)
    {
        var children = all.Where(d => d.ParentId != null).ToLookup(d => d.ParentId!.Value, d => d.Id);
        var result = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(descriptionId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!result.Add(id)) continue;
            foreach (var c in children[id]) stack.Push(c);
        }
        return result;
    }

    /// <summary>
    /// The description tree (only branches that have templates among <paramref name="rows"/>),
    /// in tree order, with counts; the first entry is "(All descriptions)".
    /// </summary>
    /// <param name="rows">The templates to count (e.g. one manufacturer's).</param>
    /// <param name="all">Every description.</param>
    public static List<TemplateAuditDescription> DescriptionTree(IReadOnlyCollection<TemplateAuditRow> rows, IReadOnlyList<Description> all)
    {
        var byId = all.ToDictionary(d => d.Id);
        var direct = rows.GroupBy(r => r.DescriptionId).ToDictionary(g => g.Key, g => g.Count());
        var total = new Dictionary<int, int>();
        foreach (var (id, n) in direct)
        {
            var seen = new HashSet<int>();
            int? cur = id;
            while (cur is int c && seen.Add(c))
            {
                total[c] = total.GetValueOrDefault(c) + n;
                cur = byId.TryGetValue(c, out var d) ? d.ParentId : null;
            }
        }

        var result = new List<TemplateAuditDescription> { new(null, string.Empty, 0, rows.Count, 0) };
        var children = all.Where(d => d.ParentId != null && byId.ContainsKey(d.ParentId.Value)).ToLookup(d => d.ParentId!.Value);
        void Walk(Description d, int depth)
        {
            if (!total.ContainsKey(d.Id)) return;
            result.Add(new TemplateAuditDescription(d.Id, Path(d.Id, byId), depth, total[d.Id], direct.GetValueOrDefault(d.Id)));
            foreach (var c in children[d.Id].OrderBy(x => x.SortOrder).ThenBy(x => x.DescriptionText, StringComparer.OrdinalIgnoreCase))
                Walk(c, depth + 1);
        }
        foreach (var root in all.Where(d => d.ParentId == null || !byId.ContainsKey(d.ParentId.Value))
                     .OrderBy(x => x.SortOrder).ThenBy(x => x.DescriptionText, StringComparer.OrdinalIgnoreCase))
            Walk(root, 0);

        // Templates whose description no longer exists.
        foreach (var id in direct.Keys.Where(id => !byId.ContainsKey(id)))
            result.Add(new TemplateAuditDescription(id, $"(missing description #{id})", 0, direct[id], direct[id]));
        return result;
    }

    /// <summary>Filters the rows.</summary>
    /// <param name="rows">All rows.</param>
    /// <param name="manufacturerId">Only this manufacturer (null = all).</param>
    /// <param name="descriptionIds">Only these descriptions (null = all).</param>
    /// <param name="status">Only this status (null = all).</param>
    /// <param name="search">Text in the template number, description, linked model numbers or note.</param>
    /// <param name="onlyItemsDiffer">Only templates whose linked items have another description.</param>
    public static List<TemplateAuditRow> Filter(
        IEnumerable<TemplateAuditRow> rows, int? manufacturerId, ISet<int>? descriptionIds,
        TemplateAuditStatus? status, string? search, bool onlyItemsDiffer)
    {
        var s = search?.Trim();
        return rows.Where(r =>
                (manufacturerId == null || r.ManufacturerId == manufacturerId) &&
                (descriptionIds == null || descriptionIds.Contains(r.DescriptionId)) &&
                (status == null || r.Status == status) &&
                (!onlyItemsDiffer || r.ItemDescriptionsDiffer) &&
                (string.IsNullOrEmpty(s) ||
                 r.TemplateNumber.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                 r.DescriptionPath.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                 (r.Mark?.Note?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                 r.Items.Any(i => i.ModelNumber.Contains(s, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    /// <summary>Loads the audit marks by template id.</summary>
    /// <param name="ctx">An open context.</param>
    public static Dictionary<int, TemplateAuditMark> LoadMarks(AppDbContext ctx)
    {
        var raw = new AppSettingRepository(ctx).GetValue(SettingKey);
        if (string.IsNullOrWhiteSpace(raw)) return new Dictionary<int, TemplateAuditMark>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, TemplateAuditMark>>(raw, Json) ?? new Dictionary<int, TemplateAuditMark>();
        }
        catch (JsonException)
        {
            return new Dictionary<int, TemplateAuditMark>();
        }
    }

    /// <summary>
    /// Marks templates (Not reviewed removes the mark unless it has a note). A null note keeps
    /// each template's existing note.
    /// </summary>
    /// <param name="ctx">An open context.</param>
    /// <param name="templateIds">The templates.</param>
    /// <param name="status">The status.</param>
    /// <param name="note">The note (null = keep; blank = clear).</param>
    /// <param name="markedBy">Who marked them.</param>
    public static void Mark(AppDbContext ctx, IEnumerable<int> templateIds, TemplateAuditStatus status, string? note, string? markedBy)
    {
        var marks = LoadMarks(ctx);
        foreach (var id in templateIds)
        {
            var keepNote = note == null ? marks.GetValueOrDefault(id)?.Note : (string.IsNullOrWhiteSpace(note) ? null : note.Trim());
            if (status == TemplateAuditStatus.NotReviewed && keepNote == null)
            {
                marks.Remove(id);
                continue;
            }
            marks[id] = new TemplateAuditMark { Status = status, Note = keepNote, MarkedAt = DateTime.UtcNow, MarkedBy = markedBy };
        }
        // Forget marks on templates that were deleted.
        var existing = ctx.IndividualTemplates.Select(t => t.Id).ToHashSet();
        foreach (var gone in marks.Keys.Where(k => !existing.Contains(k)).ToList()) marks.Remove(gone);
        new AppSettingRepository(ctx).SetValue(SettingKey, JsonSerializer.Serialize(marks, Json));
    }

    /// <summary>
    /// Gives templates a new description. With <paramref name="alsoItems"/>, linked hardware items
    /// that had the template's old description move too (items with another description are left).
    /// </summary>
    /// <param name="ctx">An open context.</param>
    /// <param name="templateIds">The templates.</param>
    /// <param name="descriptionId">The new description.</param>
    /// <param name="alsoItems">Also move the linked items that had the same description.</param>
    /// <returns>How many templates and items changed.</returns>
    public static (int Templates, int Items) SetDescription(AppDbContext ctx, IEnumerable<int> templateIds, int descriptionId, bool alsoItems)
    {
        if (!ctx.Descriptions.Any(d => d.Id == descriptionId))
            throw new InvalidOperationException("That description doesn't exist.");
        var ids = templateIds.ToHashSet();
        var templates = ctx.IndividualTemplates.Where(t => ids.Contains(t.Id)).ToList();
        int t = 0, i = 0;
        foreach (var tpl in templates)
        {
            var old = tpl.DescriptionId;
            if (old == descriptionId) continue;
            tpl.DescriptionId = descriptionId;
            t++;
            if (!alsoItems) continue;
            var itemIds = ctx.HardwareItemTemplates.Where(l => l.IndividualTemplateId == tpl.Id).Select(l => l.HardwareItemId).Distinct().ToList();
            foreach (var item in ctx.HardwareItems.Where(h => itemIds.Contains(h.Id) && h.DescriptionId == old).ToList())
            {
                item.DescriptionId = descriptionId;
                i++;
            }
        }
        ctx.SaveChanges();
        return (t, i);
    }

    /// <summary>The rows as CSV (for a spreadsheet).</summary>
    /// <param name="rows">The rows.</param>
    public static string ToCsv(IEnumerable<TemplateAuditRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Manufacturer,Template Number,Description,Status,Note,Door Material,Pages To Print,Linked Items,Linked Item Descriptions,Items Differ,Job Only,Online Link,Local File");
        foreach (var r in rows)
        {
            var cells = new[]
            {
                r.ManufacturerName, r.TemplateNumber, r.DescriptionPath,
                r.Status switch { TemplateAuditStatus.Ok => "OK", TemplateAuditStatus.NeedsChange => "Needs change", _ => "Not reviewed" },
                r.Mark?.Note ?? string.Empty, r.DoorMaterial, r.PagesToPrint,
                string.Join("; ", r.Items.Select(i => i.ModelNumber)),
                string.Join("; ", r.Items.Select(i => i.DescriptionPath).Distinct()),
                r.ItemDescriptionsDiffer ? "Yes" : "No",
                r.OriginJobId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                r.OnlineLink ?? string.Empty, r.LocalLink ?? string.Empty,
            };
            sb.AppendLine(string.Join(",", cells.Select(Csv)));
        }
        return sb.ToString();
    }

    private static string Csv(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
