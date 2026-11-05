using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Outcome of a single <see cref="JobHardwareRelinkService.Relink"/> call.
/// </summary>
public class RelinkResult
{
    /// <summary>Gets or sets whether the job had hardware to relink.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets a message describing the outcome (e.g. why it was skipped).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of hardware links that were removed and recreated.</summary>
    public int RelinkedCount { get; set; }

    /// <summary>Gets or sets whether a new <see cref="BulkAddDraft"/> was created for the job.</summary>
    public bool DraftCreated { get; set; }

    /// <summary>
    /// Gets or sets how many rows were repointed to a sibling <see cref="HardwareItem"/> that
    /// actually has template links (the real fix for post-migration "unlinked" rows — see remarks
    /// on <see cref="JobHardwareRelinkService"/>).
    /// </summary>
    public int RepointedCount { get; set; }

    /// <summary>
    /// Gets or sets how many rows still have no linked templates after relinking — i.e. no
    /// sibling <see cref="HardwareItem"/> with matching manufacturer/description/model number
    /// could be found that has any template links either.
    /// </summary>
    public int StillUnlinkedCount { get; set; }
}

/// <summary>
/// Repairs stale hardware-template links left over from the SQLite-to-SQL-Server migration.
/// A broken migration run can renumber <c>HardwareItems</c>/<c>IndividualTemplates</c>/
/// <c>HardwareItemTemplates</c> independently, leaving a <see cref="JobHardware"/> row pointing
/// at a "duplicate" <see cref="HardwareItem"/> that has no (or the wrong) template links, while
/// another row with the same manufacturer/description/model number does have valid links (see
/// <c>HardwareItemRepository.FindDuplicate</c> remarks for background on this exact failure mode).
/// Simply recreating <see cref="JobHardware"/> rows with the same <c>HardwareItemId</c> does not
/// fix this, so for every row whose current hardware item has no linked templates, this service
/// looks for a sibling <see cref="HardwareItem"/> (same manufacturer, description, and model
/// number) that does have links and repoints the row at it. Rows are then removed and recreated
/// (clearing any other orphaned state) using the resolved hardware item id. Before removing
/// anything, ensures a <see cref="BulkAddDraft"/> exists for the job so the current hardware
/// list is preserved ("filled in") even if the job never had one.
/// </summary>
public class JobHardwareRelinkService
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Removes and recreates the base hardware links for <paramref name="jobId"/>, ensuring a
    /// bulk-add draft exists first.
    /// </summary>
    /// <param name="ctx">The database context to operate on.</param>
    /// <param name="jobId">The job whose hardware should be relinked.</param>
    public RelinkResult Relink(AppDbContext ctx, int jobId)
    {
        var rows = ctx.JobHardware
            .Include(jh => jh.HardwareItem)
            .Where(jh => jh.JobId == jobId && jh.ReleaseId == null)
            .OrderBy(jh => jh.Id)
            .ToList();

        if (rows.Count == 0)
        {
            return new RelinkResult
            {
                Success = false,
                Message = "No hardware to relink."
            };
        }

        var draftRepo = new BulkAddDraftRepository(ctx);
        var draftCreated = false;
        if (draftRepo.GetByJob(jobId) == null)
        {
            var draftJson = BuildDraftJson(rows);
            draftRepo.Upsert(jobId, draftJson);
            draftCreated = true;
        }

        // Count of global (JobId == null) template links per HardwareItemId, used to detect
        // which hardware items are genuinely unlinked and which siblings can repair them.
        var linkCountByItemId = ctx.HardwareItemTemplates
            .Where(hit => hit.JobId == null)
            .ToList()
            .GroupBy(hit => hit.HardwareItemId)
            .ToDictionary(g => g.Key, g => g.Count());

        var repointedCount = 0;
        var stillUnlinkedCount = 0;

        // Snapshot before deleting, resolving each row to a sibling hardware item with real
        // template links when the row's current item has none.
        var snapshot = new List<(int HardwareItemId, string? CustomDescription, string? Remarks, string? CalloutRemarks)>();
        foreach (var r in rows)
        {
            var resolvedId = r.HardwareItemId;
            if (!linkCountByItemId.ContainsKey(resolvedId))
            {
                var sibling = FindSiblingWithTemplates(ctx, r.HardwareItem, linkCountByItemId);
                if (sibling != null)
                {
                    resolvedId = sibling.Value;
                    repointedCount++;
                }
                else
                {
                    stillUnlinkedCount++;
                }
            }

            snapshot.Add((resolvedId, r.CustomDescription, r.Remarks, r.CalloutRemarks));
        }

        ctx.JobHardware.RemoveRange(rows);
        ctx.SaveChanges();

        foreach (var s in snapshot)
        {
            ctx.JobHardware.Add(new JobHardware
            {
                JobId             = jobId,
                HardwareItemId    = s.HardwareItemId,
                CustomDescription = s.CustomDescription,
                Remarks           = s.Remarks,
                CalloutRemarks    = s.CalloutRemarks,
                ReleaseId         = null
            });
        }
        ctx.SaveChanges();

        var message = $"Relinked {snapshot.Count} hardware item(s)"
            + (repointedCount > 0 ? $", repointed {repointedCount} to a correctly-linked duplicate" : string.Empty)
            + (stillUnlinkedCount > 0 ? $", {stillUnlinkedCount} still have no templates linked" : string.Empty)
            + ".";

        return new RelinkResult
        {
            Success            = true,
            Message            = message,
            RelinkedCount      = snapshot.Count,
            DraftCreated       = draftCreated,
            RepointedCount     = repointedCount,
            StillUnlinkedCount = stillUnlinkedCount
        };
    }

    /// <summary>
    /// Looks for a different <see cref="HardwareItem"/> with the same manufacturer, description,
    /// and (trimmed, case-insensitive) model number as <paramref name="item"/> that has at least
    /// one global template link, preferring the one with the most links. Mirrors
    /// <c>HardwareItemRepository.FindDuplicate</c>'s matching rule.
    /// </summary>
    private static int? FindSiblingWithTemplates(
        AppDbContext ctx,
        HardwareItem item,
        IReadOnlyDictionary<int, int> linkCountByItemId)
    {
        var normalizedModelNumber = item.ModelNumber?.Trim() ?? string.Empty;

        return ctx.HardwareItems
            .Where(h =>
                h.Id != item.Id &&
                h.ManufacturerId == item.ManufacturerId &&
                h.DescriptionId == item.DescriptionId)
            .AsEnumerable()
            .Where(h => string.Equals(h.ModelNumber?.Trim(), normalizedModelNumber, StringComparison.OrdinalIgnoreCase))
            .Where(h => linkCountByItemId.ContainsKey(h.Id))
            .OrderByDescending(h => linkCountByItemId[h.Id])
            .Select(h => (int?)h.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Builds a version-2 bulk-add draft JSON payload from the job's current hardware rows,
    /// grouped by hardware item (mirroring the grouping used when importing hardware between jobs).
    /// </summary>
    private static string BuildDraftJson(IReadOnlyList<JobHardware> rows)
    {
        var orderedIds = new List<int>();
        var groups = new Dictionary<int, List<JobHardware>>();
        foreach (var row in rows)
        {
            if (!groups.ContainsKey(row.HardwareItemId))
            {
                orderedIds.Add(row.HardwareItemId);
                groups[row.HardwareItemId] = new List<JobHardware>();
            }
            groups[row.HardwareItemId].Add(row);
        }

        var manufacturerIds = new List<int>();
        var items = new List<object>();
        foreach (var hwId in orderedIds)
        {
            var labelRows = groups[hwId];
            var hw = labelRows[0].HardwareItem;

            if (!manufacturerIds.Contains(hw.ManufacturerId))
                manufacturerIds.Add(hw.ManufacturerId);

            items.Add(new
            {
                manufacturerId = hw.ManufacturerId,
                descriptionId = hw.DescriptionId,
                modelNumber = hw.ModelNumber,
                hardwareItemRemarks = hw.Remarks,
                labels = labelRows.Select(r => new
                {
                    customLabel = r.CustomDescription ?? string.Empty,
                    remarks = r.Remarks
                }).ToList()
            });
        }

        var draft = new
        {
            version = 2,
            manufacturerIds,
            items
        };

        return JsonSerializer.Serialize(draft, _jsonOptions);
    }
}
