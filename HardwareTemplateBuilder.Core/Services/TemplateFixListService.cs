using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>A template that failed in the last full template refresh, as it was at that time.</summary>
public class FailedTemplateRecord
{
    /// <summary>Gets or sets the template's ID.</summary>
    public int TemplateId { get; set; }

    /// <summary>Gets or sets the template number, for display.</summary>
    public string TemplateNumber { get; set; } = string.Empty;

    /// <summary>Gets or sets the refresh error (download failed, not a valid PDF, ...).</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Gets or sets the template's online link at refresh time.</summary>
    public string? OnlineLink { get; set; }

    /// <summary>Gets or sets the template's local file at refresh time.</summary>
    public string? LocalLink { get; set; }
}

/// <summary>A hardware item that uses at least one failed template.</summary>
public class TemplateFixEntry
{
    /// <summary>Gets or sets the hardware item's ID.</summary>
    public int HardwareItemId { get; set; }

    /// <summary>Gets or sets the failed templates linked to this item.</summary>
    public List<int> FailedTemplateIds { get; set; } = new();

    /// <summary>
    /// Gets or sets the item's template link IDs at refresh time; any link added later means the
    /// user has given the item a new template.
    /// </summary>
    public List<int> LinkIdsAtRefresh { get; set; } = new();
}

/// <summary>The stored fix list (saved as JSON in the <c>AppSetting</c> table).</summary>
public class TemplateFixList
{
    /// <summary>Gets or sets when the refresh that produced this list ran (UTC).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets the templates that failed.</summary>
    public List<FailedTemplateRecord> Templates { get; set; } = new();

    /// <summary>Gets or sets the hardware items still to fix.</summary>
    public List<TemplateFixEntry> Items { get; set; } = new();
}

/// <summary>One row of the "Templates to Fix" page.</summary>
public class TemplateFixItem
{
    /// <summary>Gets the hardware item's ID.</summary>
    public int HardwareItemId { get; init; }

    /// <summary>Gets the manufacturer name.</summary>
    public string ManufacturerName { get; init; } = string.Empty;

    /// <summary>Gets the item's description path (e.g. "Hinges > Butt").</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets the model number.</summary>
    public string ModelNumber { get; init; } = string.Empty;

    /// <summary>Gets the failed templates still linked to the item, with their errors.</summary>
    public IReadOnlyList<FailedTemplateRecord> FailedTemplates { get; init; } = Array.Empty<FailedTemplateRecord>();
}

/// <summary>
/// Keeps the list of hardware items whose templates failed in the last full template refresh
/// (download error or not a valid PDF), for the "Templates to Fix" page in both apps.
/// <para>
/// <see cref="TemplateRefreshService.RefreshAsync"/> calls <see cref="Record"/> at the end of every
/// full refresh, replacing the previous list. An item drops off on its own once it is fixed:
/// it gets a new template link, the failed template is unlinked from it, or the failed template
/// itself is repaired (its online link changed, or it now points at a different local file that
/// exists). Items can also be dismissed by hand.
/// </para>
/// </summary>
public static class TemplateFixListService
{
    /// <summary>The <c>AppSetting</c> key the list is stored under.</summary>
    public const string SettingKey = "TemplateFixList";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>
    /// Replaces the stored list with the items that use any of <paramref name="failures"/>.
    /// An empty <paramref name="failures"/> clears the list.
    /// </summary>
    public static void Record(AppDbContext context, IReadOnlyList<RefreshFailure> failures)
    {
        var failedIds = failures.Select(f => f.TemplateId).ToHashSet();
        var templates = context.IndividualTemplates.AsNoTracking()
            .Where(t => failedIds.Contains(t.Id))
            .ToList()
            .Select(t => new FailedTemplateRecord
            {
                TemplateId = t.Id,
                TemplateNumber = t.TemplateNumber,
                Error = failures.First(f => f.TemplateId == t.Id).ErrorMessage,
                OnlineLink = t.OnlineLink,
                LocalLink = t.LocalLink,
            })
            .ToList();

        var links = context.HardwareItemTemplates.AsNoTracking().ToList();
        var items = links
            .Where(l => failedIds.Contains(l.IndividualTemplateId))
            .Select(l => l.HardwareItemId)
            .Distinct()
            .OrderBy(id => id)
            .Select(itemId => new TemplateFixEntry
            {
                HardwareItemId = itemId,
                FailedTemplateIds = links.Where(l => l.HardwareItemId == itemId && failedIds.Contains(l.IndividualTemplateId))
                    .Select(l => l.IndividualTemplateId).Distinct().ToList(),
                LinkIdsAtRefresh = links.Where(l => l.HardwareItemId == itemId).Select(l => l.Id).ToList(),
            })
            .ToList();

        Save(context, new TemplateFixList { CreatedUtc = DateTime.UtcNow, Templates = templates, Items = items });
    }

    /// <summary>
    /// Returns the items still needing a template, dropping (and saving) any that have been fixed
    /// since the refresh or whose hardware item no longer exists.
    /// </summary>
    public static List<TemplateFixItem> GetOutstanding(AppDbContext context)
    {
        var list = Load(context);
        if (list.Items.Count == 0) return new List<TemplateFixItem>();

        var itemIds = list.Items.Select(i => i.HardwareItemId).ToList();
        var templateIds = list.Templates.Select(t => t.TemplateId).ToList();

        var hardware = context.HardwareItems.AsNoTracking()
            .Include(h => h.Manufacturer)
            .Where(h => itemIds.Contains(h.Id))
            .ToDictionary(h => h.Id);
        var links = context.HardwareItemTemplates.AsNoTracking()
            .Where(l => itemIds.Contains(l.HardwareItemId))
            .ToList();
        var currentTemplates = context.IndividualTemplates.AsNoTracking()
            .Where(t => templateIds.Contains(t.Id))
            .ToDictionary(t => t.Id);
        var descriptions = context.Descriptions.AsNoTracking().ToDictionary(d => d.Id);
        var failedById = list.Templates.ToDictionary(t => t.TemplateId);

        var stillBroken = new List<TemplateFixEntry>();
        var result = new List<TemplateFixItem>();
        foreach (var entry in list.Items)
        {
            if (!hardware.TryGetValue(entry.HardwareItemId, out var item)) continue;
            var itemLinks = links.Where(l => l.HardwareItemId == entry.HardwareItemId).ToList();

            // A template linked since the refresh means the user gave the item a new one.
            if (itemLinks.Any(l => !entry.LinkIdsAtRefresh.Contains(l.Id))) continue;

            var brokenStillLinked = entry.FailedTemplateIds
                .Where(id => itemLinks.Any(l => l.IndividualTemplateId == id))
                .Where(id => failedById.TryGetValue(id, out var failed) && !IsRepaired(failed, currentTemplates.GetValueOrDefault(id)))
                .ToList();
            if (brokenStillLinked.Count == 0) continue;

            stillBroken.Add(entry);
            result.Add(new TemplateFixItem
            {
                HardwareItemId = item.Id,
                ManufacturerName = item.Manufacturer?.ManufacturerName ?? string.Empty,
                Description = DescriptionPath(item.DescriptionId, descriptions),
                ModelNumber = item.ModelNumber,
                FailedTemplates = brokenStillLinked.Select(id => failedById[id]).ToList(),
            });
        }

        if (stillBroken.Count != list.Items.Count)
        {
            list.Items = stillBroken;
            Save(context, list);
        }

        return result
            .OrderBy(r => r.ManufacturerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Description, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ModelNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Removes one item from the list by hand.</summary>
    public static void Dismiss(AppDbContext context, int hardwareItemId)
    {
        var list = Load(context);
        if (list.Items.RemoveAll(i => i.HardwareItemId == hardwareItemId) > 0)
            Save(context, list);
    }

    /// <summary>When the stored list was made, or null if there is none.</summary>
    public static DateTime? CreatedUtc(AppDbContext context)
    {
        var list = Load(context);
        return list.Items.Count > 0 || list.Templates.Count > 0 ? list.CreatedUtc : null;
    }

    /// <summary>
    /// A failed template counts as repaired when its online link was changed, or when it now
    /// points at a different local file that exists. Deleted templates count as repaired too
    /// (the item can't be fixed by the old template any more, so it shouldn't linger).
    /// </summary>
    private static bool IsRepaired(FailedTemplateRecord failed, IndividualTemplate? current)
    {
        if (current == null) return true;
        if (!string.Equals(current.OnlineLink ?? string.Empty, failed.OnlineLink ?? string.Empty, StringComparison.Ordinal))
            return true;
        var newLocal = current.LocalLink;
        return !string.IsNullOrWhiteSpace(newLocal)
               && !string.Equals(newLocal, failed.LocalLink, StringComparison.OrdinalIgnoreCase)
               && File.Exists(newLocal);
    }

    private static string DescriptionPath(int descriptionId, IReadOnlyDictionary<int, Description> all)
    {
        var parts = new List<string>();
        int? id = descriptionId;
        while (id.HasValue && all.TryGetValue(id.Value, out var d) && parts.Count < 20)
        {
            parts.Insert(0, d.DescriptionText);
            id = d.ParentId;
        }
        return string.Join(" > ", parts);
    }

    private static TemplateFixList Load(AppDbContext context)
    {
        var raw = new AppSettingRepository(context).GetValue(SettingKey);
        if (string.IsNullOrWhiteSpace(raw)) return new TemplateFixList();
        try
        {
            return JsonSerializer.Deserialize<TemplateFixList>(raw, Json) ?? new TemplateFixList();
        }
        catch (JsonException)
        {
            return new TemplateFixList();
        }
    }

    private static void Save(AppDbContext context, TemplateFixList list) =>
        new AppSettingRepository(context).SetValue(SettingKey, JsonSerializer.Serialize(list, Json));
}
