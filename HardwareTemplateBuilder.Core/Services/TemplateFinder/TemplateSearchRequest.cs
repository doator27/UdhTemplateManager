using System;
using System.Collections.Generic;
using System.Linq;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Services.Import;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services.TemplateFinder;

/// <summary>
/// What to look for when finding a template PDF online: the manufacturer, the model number /
/// template number, labels used on past jobs, the description, and (for a broken template) the
/// old link. Built from the database with <see cref="ForHardwareItem"/> or <see cref="ForTemplate"/>.
/// </summary>
public sealed class TemplateSearchRequest
{
    /// <summary>Gets or sets the manufacturer id.</summary>
    public int ManufacturerId { get; set; }

    /// <summary>Gets or sets the manufacturer name.</summary>
    public string ManufacturerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the hardware item's model number, if searching for an item.</summary>
    public string? ModelNumber { get; set; }

    /// <summary>Gets or sets the template number, if known (broken template).</summary>
    public string? TemplateNumber { get; set; }

    /// <summary>Gets or sets the description path, e.g. "Closers &gt; Surface".</summary>
    public string? Description { get; set; }

    /// <summary>Gets the other callouts to look for: labels used on past jobs, model numbers of items using the template.</summary>
    public List<string> Labels { get; } = new();

    /// <summary>Gets or sets the old link of a broken template.</summary>
    public string? BrokenLink { get; set; }

    /// <summary>Gets or sets the hardware item being searched for, if any.</summary>
    public int? HardwareItemId { get; set; }

    /// <summary>Gets or sets the template being repaired, if any.</summary>
    public int? TemplateId { get; set; }

    /// <summary>One line describing the search, for the window title.</summary>
    public string Summary =>
        $"{ManufacturerName} {ModelNumber ?? TemplateNumber}".Trim() +
        (TemplateNumber != null && ModelNumber != null ? $" (template {TemplateNumber})" : string.Empty);

    /// <summary>
    /// The search terms, most specific first: model number, template number, labels, each also
    /// without its size/notes; de-duplicated by comparison key.
    /// </summary>
    public List<string> Terms()
    {
        var raw = new List<string?> { ModelNumber, TemplateNumber };
        raw.AddRange(Labels);
        var result = new List<string>();
        var seen = new HashSet<string>();
        foreach (var t in raw)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            foreach (var variant in new[] { t.Trim(), ImportText.StripNotesAndSize(t.Trim()), FirstToken(t) })
            {
                var key = ImportText.Key(variant);
                if (key.Length >= 3 && key.Any(char.IsDigit) && seen.Add(key)) result.Add(variant);
            }
        }
        return result;
    }

    /// <summary>The Google search to open in the browser for this request.</summary>
    public string GoogleSearchUrl()
    {
        var what = ModelNumber ?? TemplateNumber ?? string.Empty;
        var query = $"{ManufacturerName} {what} template pdf";
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(query.Trim());
    }

    /// <summary>Builds a request for a hardware item (model number, description, past labels).</summary>
    /// <param name="ctx">An open context.</param>
    /// <param name="hardwareItemId">The item.</param>
    public static TemplateSearchRequest ForHardwareItem(AppDbContext ctx, int hardwareItemId)
    {
        var item = ctx.HardwareItems.Include(h => h.Manufacturer).AsNoTracking().First(h => h.Id == hardwareItemId);
        var request = new TemplateSearchRequest
        {
            HardwareItemId = item.Id,
            ManufacturerId = item.ManufacturerId,
            ManufacturerName = item.Manufacturer.ManufacturerName,
            ModelNumber = item.ModelNumber,
            Description = DescriptionPath(ctx, item.DescriptionId),
        };
        request.Labels.AddRange(ctx.JobHardware
            .Where(jh => jh.HardwareItemId == item.Id && jh.CustomDescription != null && jh.CustomDescription != "")
            .GroupBy(jh => jh.CustomDescription!)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(8)
            .ToList());
        return request;
    }

    /// <summary>
    /// Builds a request for an existing template (e.g. one whose link broke): its number, old link,
    /// and the model numbers of the hardware items that use it.
    /// </summary>
    /// <param name="ctx">An open context.</param>
    /// <param name="templateId">The template.</param>
    public static TemplateSearchRequest ForTemplate(AppDbContext ctx, int templateId)
    {
        var t = ctx.IndividualTemplates.Include(x => x.Manufacturer).AsNoTracking().First(x => x.Id == templateId);
        var request = new TemplateSearchRequest
        {
            TemplateId = t.Id,
            ManufacturerId = t.ManufacturerId,
            ManufacturerName = t.Manufacturer.ManufacturerName,
            TemplateNumber = t.TemplateNumber,
            Description = DescriptionPath(ctx, t.DescriptionId),
            BrokenLink = string.IsNullOrWhiteSpace(t.OnlineLink) ? null : t.OnlineLink,
        };
        var models = ctx.HardwareItemTemplates
            .Where(l => l.IndividualTemplateId == t.Id)
            .Select(l => l.HardwareItem.ModelNumber)
            .Distinct()
            .Take(6)
            .ToList();
        if (models.Count > 0) request.ModelNumber = models[0];
        request.Labels.AddRange(models.Skip(1));
        return request;
    }

    private static string FirstToken(string text) =>
        text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

    private static string? DescriptionPath(AppDbContext ctx, int descriptionId)
    {
        var parts = new List<string>();
        var seen = new HashSet<int>();
        int? id = descriptionId;
        while (id is int current && seen.Add(current))
        {
            var d = ctx.Descriptions.AsNoTracking().FirstOrDefault(x => x.Id == current);
            if (d == null) break;
            parts.Insert(0, d.DescriptionText);
            id = d.ParentId;
        }
        return parts.Count > 0 ? string.Join(" > ", parts) : null;
    }
}
