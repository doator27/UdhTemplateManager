using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.Import;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services.Catalogs;

/// <summary>A template number a catalog page gives for a callout.</summary>
/// <param name="Number">The template number as written (or as stored, when it's already in the database).</param>
/// <param name="TemplateId">The matching template in the database, if any.</param>
/// <param name="HardwareItemIds">Hardware items that already use that template.</param>
/// <param name="CatalogId">The catalog.</param>
/// <param name="PageNumber">The page.</param>
public sealed record CatalogTemplateRef(string Number, int? TemplateId, IReadOnlyList<int> HardwareItemIds, int CatalogId, int PageNumber)
{
    /// <summary>List text: "5-3018 (already in your templates)  - page 47".</summary>
    public string DisplayText => $"{Number}{(TemplateId != null ? " (already in your templates)" : string.Empty)}  - page {PageNumber}";

    /// <inheritdoc />
    public override string ToString() => DisplayText;
}

/// <summary>What an option code in a callout means, according to a catalog page.</summary>
/// <param name="Code">The code, e.g. "EDA".</param>
/// <param name="Meaning">What it means, e.g. "Extra Duty Arm".</param>
/// <param name="CatalogId">The catalog.</param>
/// <param name="PageNumber">The page.</param>
public sealed record CatalogOption(string Code, string Meaning, int CatalogId, int PageNumber)
{
    /// <inheritdoc />
    public override string ToString() => $"{Code} = {Meaning}  (page {PageNumber})";
}

/// <summary>One catalog page that matches a callout, with what was read from it.</summary>
public sealed class CatalogHit
{
    /// <summary>Gets or sets the catalog.</summary>
    public int CatalogId { get; set; }

    /// <summary>Gets or sets the catalog's title.</summary>
    public string CatalogTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the 1-based page number.</summary>
    public int PageNumber { get; set; }

    /// <summary>Gets or sets the match score (higher first).</summary>
    public int Score { get; set; }

    /// <summary>Gets or sets the product description on the page, if found.</summary>
    public string? Description { get; set; }

    /// <summary>Gets the template numbers on the page.</summary>
    public List<CatalogTemplateRef> Templates { get; } = new();

    /// <summary>Gets or sets the lines around the model.</summary>
    public string Snippet { get; set; } = string.Empty;

    /// <summary>List text: "Page 47 - LCN Closers Catalog: 4040XP Heavy Duty Closer (template 5-3018)".</summary>
    public string DisplayText =>
        $"Page {PageNumber} - {CatalogTitle}{(Description != null ? $": {Description}" : string.Empty)}" +
        (Templates.Count > 0 ? $"  (template {string.Join(", ", Templates.Select(t => t.Number))})" : string.Empty);

    /// <inheritdoc />
    public override string ToString() => DisplayText;
}

/// <summary>Everything the product catalogs say about one callout.</summary>
public sealed class CatalogLookupResult
{
    /// <summary>Gets or sets the callout looked up.</summary>
    public string Callout { get; set; } = string.Empty;

    /// <summary>Gets or sets the manufacturer.</summary>
    public int ManufacturerId { get; set; }

    /// <summary>Gets or sets how many indexed catalogs the manufacturer has.</summary>
    public int CatalogCount { get; set; }

    /// <summary>Gets the matching pages, best first.</summary>
    public List<CatalogHit> Hits { get; } = new();

    /// <summary>Gets the option meanings found (first page that explains each code).</summary>
    public List<CatalogOption> Options { get; } = new();

    /// <summary>Gets the callout's option codes that no page explained.</summary>
    public List<string> UnexplainedCodes { get; } = new();

    /// <summary>Gets or sets the description category that fits the catalog wording, if any.</summary>
    public int? SuggestedDescriptionId { get; set; }

    /// <summary>True when at least one page matched.</summary>
    public bool Found => Hits.Count > 0;

    /// <summary>The best description found.</summary>
    public string? Description => Hits.Select(h => h.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));

    /// <summary>All template numbers found, known templates first, then by page score.</summary>
    public List<CatalogTemplateRef> Templates =>
        Hits.SelectMany(h => h.Templates)
            .GroupBy(t => ImportText.Key(t.Number))
            .Select(g => g.First())
            .OrderBy(t => t.TemplateId == null ? 1 : 0)
            .ToList();

    /// <summary>Several lines summing up the result, for the screens that show catalog hints.</summary>
    public string Summary
    {
        get
        {
            if (CatalogCount == 0) return "No product catalogs saved for this manufacturer (Maintenance > Product Catalogs).";
            if (!Found) return $"Not found in this manufacturer's {CatalogCount} catalog(s).";
            var sb = new StringBuilder();
            var best = Hits[0];
            sb.Append($"Catalog: {best.CatalogTitle}, page {best.PageNumber}");
            if (Description != null) sb.Append($" - {Description}");
            var templates = Templates;
            if (templates.Count > 0) sb.Append($"\nTemplate: {string.Join(", ", templates.Take(4).Select(t => t.TemplateId != null ? $"{t.Number} (in your templates)" : t.Number))}");
            if (Options.Count > 0) sb.Append($"\nOptions: {string.Join("; ", Options.Select(o => $"{o.Code} = {o.Meaning}"))}");
            if (UnexplainedCodes.Count > 0) sb.Append($"\nNot explained: {string.Join(", ", UnexplainedCodes)}");
            return sb.ToString();
        }
    }
}

/// <summary>
/// Looks callouts up in the manufacturer's product catalogs (the page text stored by
/// <see cref="ProductCatalogService"/>): finds the pages that list the model, then reads the
/// template numbers, option meanings and description from them (<see cref="CatalogPageAnalyzer"/>).
/// Used by Find Template Online, Parse PDF into Bulk Add and the hardware item page.
/// Keeps per-manufacturer lookups cached, so one instance can serve many callouts.
/// </summary>
public sealed class CatalogLookupService
{
    private const int MaxPagesLoaded = 150;
    private const int MaxHits = 6;

    private readonly Func<AppDbContext> _contextFactory;
    private readonly Dictionary<int, Dictionary<int, string>> _catalogs = new();
    private readonly Dictionary<int, List<(int Id, string Number, List<int> Items)>> _templates = new();
    private List<Description>? _descriptions;

    /// <summary>Creates the service.</summary>
    /// <param name="contextFactory">Creates database contexts (normally <c>DatabaseInitializer.CreateContext</c>).</param>
    public CatalogLookupService(Func<AppDbContext> contextFactory) => _contextFactory = contextFactory;

    /// <summary>True when the manufacturer has at least one catalog whose text has been read.</summary>
    /// <param name="manufacturerId">The manufacturer.</param>
    public bool HasCatalogs(int manufacturerId) => Catalogs(manufacturerId).Count > 0;

    /// <summary>Looks a callout up.</summary>
    /// <param name="manufacturerId">The manufacturer whose catalogs to search.</param>
    /// <param name="callout">The callout or model number, e.g. "4040XP EDA RW/PA AL".</param>
    public CatalogLookupResult Lookup(int manufacturerId, string callout)
    {
        var result = new CatalogLookupResult { Callout = callout ?? string.Empty, ManufacturerId = manufacturerId };
        var catalogs = Catalogs(manufacturerId);
        result.CatalogCount = catalogs.Count;
        if (catalogs.Count == 0 || string.IsNullOrWhiteSpace(callout)) return result;

        var options = CatalogPageAnalyzer.OptionCodes(callout);
        var catalogIds = catalogs.Keys.ToList();
        using var ctx = _contextFactory();

        string modelKey = string.Empty;
        List<ProductCatalogPage> pages = new();
        foreach (var key in CatalogPageAnalyzer.ModelKeys(callout))
        {
            pages = ctx.ProductCatalogPages.AsNoTracking()
                .Where(p => catalogIds.Contains(p.CatalogId) && p.SearchKey.Contains(key))
                .OrderBy(p => p.CatalogId).ThenBy(p => p.PageNumber)
                .Take(MaxPagesLoaded)
                .ToList();
            modelKey = key;
            if (pages.Any(p => CatalogPageAnalyzer.ScorePage(p.Text, key, options) >= 35)) break;
        }
        if (pages.Count == 0) { result.UnexplainedCodes.AddRange(options); return result; }

        var known = Templates(manufacturerId);
        var scored = pages
            .Select(p => (Page: p, Score: CatalogPageAnalyzer.ScorePage(p.Text, modelKey, options)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Page.PageNumber)
            .Take(MaxHits)
            .ToList();

        foreach (var (page, score) in scored)
        {
            var hit = new CatalogHit
            {
                CatalogId = page.CatalogId,
                CatalogTitle = catalogs[page.CatalogId],
                PageNumber = page.PageNumber,
                Score = score,
                Description = CatalogPageAnalyzer.Description(page.Text, modelKey, options),
                Snippet = CatalogPageAnalyzer.Snippet(page.Text, modelKey),
            };
            foreach (var number in CatalogPageAnalyzer.TemplateNumbers(page.Text, modelKey, known.Select(k => k.Number)))
            {
                var match = known.FirstOrDefault(k => ImportText.Key(k.Number) == ImportText.Key(number));
                hit.Templates.Add(new CatalogTemplateRef(number, match.Number != null ? match.Id : null,
                    match.Number != null ? match.Items : new List<int>(), page.CatalogId, page.PageNumber));
            }
            result.Hits.Add(hit);
        }

        // Option meanings: the matching pages first, then the pages next to the best ones
        // (option tables often follow the product page), then the other loaded pages.
        var order = scored.Select(s => s.Page).ToList();
        if (options.Count > 0 && scored.Count > 0)
        {
            var near = scored.Take(3)
                .SelectMany(s => Enumerable.Range(s.Page.PageNumber - 2, 5).Select(n => (s.Page.CatalogId, n)))
                .Distinct()
                .ToList();
            var nearIds = near.Select(n => n.CatalogId).Distinct().ToList();
            var nearNumbers = near.Select(n => n.n).Distinct().ToList();
            var extra = ctx.ProductCatalogPages.AsNoTracking()
                .Where(p => nearIds.Contains(p.CatalogId) && nearNumbers.Contains(p.PageNumber))
                .ToList()
                .Where(p => near.Contains((p.CatalogId, p.PageNumber)))
                .OrderBy(p => p.PageNumber);
            order.AddRange(extra.Where(p => order.All(o => o.Id != p.Id)));
            order.AddRange(pages.Where(p => order.All(o => o.Id != p.Id)));
        }
        foreach (var code in options)
        {
            CatalogOption? found = null;
            foreach (var p in order)
            {
                var meaning = CatalogPageAnalyzer.OptionMeaning(p.Text, code);
                if (meaning == null) continue;
                found = new CatalogOption(code, meaning, p.CatalogId, p.PageNumber);
                break;
            }
            if (found != null) result.Options.Add(found);
            else result.UnexplainedCodes.Add(code);
        }

        var wording = string.Join(" ", result.Hits.Take(2).Select(h => $"{h.Description} {h.CatalogTitle}"));
        result.SuggestedDescriptionId = CatalogPageAnalyzer.SuggestDescriptionId(Descriptions(), wording);
        return result;
    }

    private Dictionary<int, string> Catalogs(int manufacturerId)
    {
        if (_catalogs.TryGetValue(manufacturerId, out var list)) return list;
        using var ctx = _contextFactory();
        list = ctx.ProductCatalogs.AsNoTracking()
            .Where(c => c.ManufacturerId == manufacturerId && c.IndexedUtc != null && c.PageCount > 0)
            .ToDictionary(c => c.Id, c => c.Title);
        _catalogs[manufacturerId] = list;
        return list;
    }

    private List<(int Id, string Number, List<int> Items)> Templates(int manufacturerId)
    {
        if (_templates.TryGetValue(manufacturerId, out var list)) return list;
        using var ctx = _contextFactory();
        var templates = ctx.IndividualTemplates.AsNoTracking()
            .Where(t => t.ManufacturerId == manufacturerId && t.OriginJobId == null)
            .Select(t => new { t.Id, t.TemplateNumber })
            .ToList();
        var links = ctx.HardwareItemTemplates.AsNoTracking()
            .Where(l => l.JobId == null && l.IndividualTemplate.ManufacturerId == manufacturerId && l.HardwareItem.IsActive)
            .Select(l => new { l.IndividualTemplateId, l.HardwareItemId })
            .ToList()
            .GroupBy(l => l.IndividualTemplateId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.HardwareItemId).Distinct().ToList());
        list = templates
            .Select(t => (t.Id, t.TemplateNumber, links.TryGetValue(t.Id, out var items) ? items : new List<int>()))
            .ToList();
        _templates[manufacturerId] = list;
        return list;
    }

    private List<Description> Descriptions()
    {
        if (_descriptions != null) return _descriptions;
        using var ctx = _contextFactory();
        _descriptions = ctx.Descriptions.AsNoTracking().ToList();
        return _descriptions;
    }
}

/// <summary>What the user picked in the catalog lookup window.</summary>
/// <param name="TemplateNumber">The template number to use, if any.</param>
/// <param name="TemplateId">An existing template to link, if the number is already in the database.</param>
/// <param name="DescriptionId">The description (category) that fits the catalog wording, if any.</param>
/// <param name="Description">The catalog's product description, if found.</param>
public sealed record CatalogLookupChoice(string? TemplateNumber, int? TemplateId, int? DescriptionId, string? Description);
