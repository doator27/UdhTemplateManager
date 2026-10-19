using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Repairs missing hardware-item/template links (<see cref="HardwareItemTemplate"/>, global
/// links only — <c>JobId == null</c>) in the master database by comparing against a legacy
/// SQLite database. Hardware items and templates are matched between the two databases by
/// content (Manufacturer name + full Description hierarchy path + Model/Template number)
/// rather than by Id, since Ids may differ between the legacy file and the master database.
/// <para>
/// For every legacy <see cref="HardwareItemTemplate"/> link whose hardware item and template
/// both have a content match in the master database, if the master database does not already
/// have that pairing linked (as a global link), it is added to the plan to be restored.
/// Existing links are left untouched — nothing is ever removed or duplicated.
/// </para>
/// <para>
/// Call <see cref="PreviewAsync"/> first to compute a dry-run <see cref="TemplateRelinkPlan"/>
/// safe to show the user, then pass it to <see cref="ApplyAsync"/> only after explicit
/// confirmation to actually write to the master database.
/// </para>
/// </summary>
public sealed class TemplateRelinkService
{
    private readonly Func<AppDbContext> _targetContextFactory;
    private List<(int HardwareItemId, int IndividualTemplateId)>? _pendingApplyLinks;
    private string? _previewedSourcePath;

    /// <summary>Initializes a new instance of <see cref="TemplateRelinkService"/>.</summary>
    /// <param name="targetContextFactory">Creates a new <see cref="AppDbContext"/> pointed at the master database.</param>
    public TemplateRelinkService(Func<AppDbContext> targetContextFactory)
    {
        _targetContextFactory = targetContextFactory;
    }

    /// <summary>
    /// Reads <paramref name="sqliteFilePath"/> and the master database and computes a dry-run
    /// plan describing which missing hardware-item/template links would be restored. Does not
    /// write anything.
    /// </summary>
    public async Task<TemplateRelinkPlan> PreviewAsync(string sqliteFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(sqliteFilePath))
        {
            throw new FileNotFoundException("Legacy SQLite database file not found.", sqliteFilePath);
        }

        var sourceOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={sqliteFilePath};Mode=ReadOnly")
            .Options;

        await using var source = new AppDbContext(sourceOptions);
        await using var target = _targetContextFactory();

        var plan = new TemplateRelinkPlan { SourceFilePath = sqliteFilePath };

        // ── Build content-keyed lookups for both databases ─────────────────────────
        var targetDescriptions = await target.Set<Description>().AsNoTracking().ToListAsync(ct);
        var sourceDescriptions = await source.Set<Description>().AsNoTracking().ToListAsync(ct);

        var targetManufacturers = await target.Set<Manufacturer>().AsNoTracking().ToDictionaryAsync(m => m.Id, ct);
        var sourceManufacturers = await source.Set<Manufacturer>().AsNoTracking().ToDictionaryAsync(m => m.Id, ct);

        var targetHardwareItems = await target.Set<HardwareItem>().AsNoTracking().ToListAsync(ct);
        var sourceHardwareItems = await source.Set<HardwareItem>().AsNoTracking().ToListAsync(ct);

        var targetTemplates = await target.Set<IndividualTemplate>().AsNoTracking().ToListAsync(ct);
        var sourceTemplates = await source.Set<IndividualTemplate>().AsNoTracking().ToListAsync(ct);

        string HardwareKey(HardwareItem h, IReadOnlyDictionary<int, Manufacturer> mfrs, IReadOnlyList<Description> descs) =>
            $"{(mfrs.TryGetValue(h.ManufacturerId, out var m) ? m.ManufacturerName : "?")}|" +
            $"{DescriptionPathService.GetFolderPath(h.DescriptionId, descs)}|{h.ModelNumber}";

        string TemplateKey(IndividualTemplate t, IReadOnlyDictionary<int, Manufacturer> mfrs, IReadOnlyList<Description> descs) =>
            $"{(mfrs.TryGetValue(t.ManufacturerId, out var m) ? m.ManufacturerName : "?")}|" +
            $"{DescriptionPathService.GetFolderPath(t.DescriptionId, descs)}|{t.TemplateNumber}";

        var targetHardwareByKey = targetHardwareItems
            .GroupBy(h => HardwareKey(h, targetManufacturers, targetDescriptions))
            .ToDictionary(g => g.Key, g => g.First().Id);

        var targetTemplateByKey = targetTemplates
            .GroupBy(t => TemplateKey(t, targetManufacturers, targetDescriptions))
            .ToDictionary(g => g.Key, g => g.First().Id);

        var sourceHardwareById = sourceHardwareItems.ToDictionary(h => h.Id);
        var sourceTemplateById = sourceTemplates.ToDictionary(t => t.Id);

        // ── Existing global links in the master database (to avoid duplicates) ────
        var existingLinks = await target.Set<HardwareItemTemplate>()
            .AsNoTracking()
            .Where(l => l.JobId == null)
            .Select(l => new { l.HardwareItemId, l.IndividualTemplateId })
            .ToListAsync(ct);
        var existingLinkSet = existingLinks
            .Select(l => (l.HardwareItemId, l.IndividualTemplateId))
            .ToHashSet();

        // ── Legacy global links to check ───────────────────────────────────────────
        List<HardwareItemTemplate> sourceLinks;
        try
        {
            sourceLinks = await source.Set<HardwareItemTemplate>()
                .AsNoTracking()
                .Where(l => l.JobId == null)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not read HardwareItemTemplates from the legacy database: {ex.Message}", ex);
        }

        var unmatchedHardware = new HashSet<string>();
        var unmatchedTemplates = new HashSet<string>();
        var toAdd = new List<(int HardwareItemId, int IndividualTemplateId)>();

        foreach (var link in sourceLinks)
        {
            if (!sourceHardwareById.TryGetValue(link.HardwareItemId, out var srcHw))
                continue;
            if (!sourceTemplateById.TryGetValue(link.IndividualTemplateId, out var srcTpl))
                continue;

            var hwKey = HardwareKey(srcHw, sourceManufacturers, sourceDescriptions);
            var tplKey = TemplateKey(srcTpl, sourceManufacturers, sourceDescriptions);

            var hwMatched = targetHardwareByKey.TryGetValue(hwKey, out var targetHwId);
            var tplMatched = targetTemplateByKey.TryGetValue(tplKey, out var targetTplId);

            if (!hwMatched)
            {
                unmatchedHardware.Add(hwKey.Replace("|", "  /  "));
                continue;
            }
            if (!tplMatched)
            {
                unmatchedTemplates.Add(tplKey.Replace("|", "  /  "));
                continue;
            }

            var hwDescription = $"{(targetManufacturers.TryGetValue(targetHardwareItems.First(h => h.Id == targetHwId).ManufacturerId, out var tm) ? tm.ManufacturerName : "?")} — {srcHw.ModelNumber}";
            var tplDescription = $"{(targetManufacturers.TryGetValue(targetTemplates.First(t => t.Id == targetTplId).ManufacturerId, out var ttm) ? ttm.ManufacturerName : "?")} — {srcTpl.TemplateNumber}";

            var pair = (targetHwId, targetTplId);
            if (existingLinkSet.Contains(pair))
            {
                plan.AlreadyLinked.Add(new RelinkRowResult
                {
                    HardwareItemId = targetHwId,
                    HardwareItemDescription = hwDescription,
                    IndividualTemplateId = targetTplId,
                    TemplateDescription = tplDescription
                });
                continue;
            }

            // Guard against the same restored pairing being added twice if the legacy
            // database itself has duplicate link rows.
            if (toAdd.Contains(pair))
                continue;

            toAdd.Add(pair);
            plan.ToAdd.Add(new RelinkRowResult
            {
                HardwareItemId = targetHwId,
                HardwareItemDescription = hwDescription,
                IndividualTemplateId = targetTplId,
                TemplateDescription = tplDescription
            });
        }

        plan.UnmatchedHardwareItems.AddRange(unmatchedHardware.OrderBy(s => s));
        plan.UnmatchedTemplates.AddRange(unmatchedTemplates.OrderBy(s => s));

        _pendingApplyLinks = toAdd;
        _previewedSourcePath = sqliteFilePath;
        return plan;
    }

    /// <summary>
    /// Writes a plan previously produced by <see cref="PreviewAsync"/> to the master database.
    /// Must be called with the exact <see cref="TemplateRelinkPlan"/> instance returned by the
    /// most recent <see cref="PreviewAsync"/> call on this service instance. All inserts happen
    /// in a single transaction — if anything fails, nothing is written.
    /// </summary>
    public async Task ApplyAsync(TemplateRelinkPlan plan, CancellationToken ct = default)
    {
        if (_pendingApplyLinks is null || _previewedSourcePath != plan.SourceFilePath)
        {
            throw new InvalidOperationException(
                "ApplyAsync must be called with the plan returned by the most recent PreviewAsync call.");
        }

        await using var target = _targetContextFactory();
        var strategy = target.Database.CreateExecutionStrategy();

        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                target.ChangeTracker.Clear();
                await using var transaction = await target.Database.BeginTransactionAsync(ct);
                try
                {
                    foreach (var (hardwareItemId, individualTemplateId) in _pendingApplyLinks)
                    {
                        target.Set<HardwareItemTemplate>().Add(new HardwareItemTemplate
                        {
                            HardwareItemId = hardwareItemId,
                            IndividualTemplateId = individualTemplateId,
                            JobId = null
                        });
                    }

                    await target.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }
                catch
                {
                    await transaction.RollbackAsync(ct);
                    throw;
                }
            });
        }
        finally
        {
            _pendingApplyLinks = null;
            _previewedSourcePath = null;
        }
    }
}
