using System.Collections.Generic;
using System.Linq;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services;

namespace HardwareTemplateBuilder.Wpf;

/// <summary>
/// Saves bulk-added / imported hardware rows (<see cref="BulkHardwareRow"/>) to a job: creates new
/// hardware items, links staged templates (cloning other jobs' job-scoped templates), and adds one
/// <see cref="JobHardware"/> row per label to the given release. Shared by the Bulk Add hub, Import
/// Hardware and the template wizard's Finish, so rows that already have templates go straight onto
/// the job and only rows with no linked templates are stepped through in the wizard.
/// </summary>
public static class BulkHardwareSaver
{
    /// <summary>
    /// True when a row has no templates to put in a package yet: a new hardware item, or an
    /// existing one with no template linked (globally or for this job) and none staged on the row.
    /// These are the only rows the template wizard needs to show.
    /// </summary>
    /// <param name="ctx">An open database context.</param>
    /// <param name="row">The row to check.</param>
    /// <param name="jobId">The job the hardware is being added to.</param>
    public static bool NeedsTemplates(AppDbContext ctx, BulkHardwareRow row, int jobId)
    {
        if (row.PendingTemplates.Count > 0) return false;
        if (row.MatchedItem == null) return true;
        var itemId = row.MatchedItem.Id;
        return !ctx.HardwareItemTemplates.Any(hit =>
            hit.HardwareItemId == itemId && (hit.JobId == null || hit.JobId == jobId));
    }

    /// <summary>
    /// Saves every row that already has templates and returns the rows that still need them
    /// (in their original order), for the template wizard.
    /// </summary>
    /// <param name="jobId">The job to add the hardware to.</param>
    /// <param name="rows">All rows being added.</param>
    /// <param name="releaseId">The job release to add to (null = base list).</param>
    /// <param name="skipped">Labels of rows already on the job (not added again).</param>
    public static List<BulkHardwareRow> SaveRowsWithTemplates(
        int jobId, IReadOnlyList<BulkHardwareRow> rows, int? releaseId, out List<string> skipped)
    {
        List<BulkHardwareRow> needTemplates;
        using (var ctx = DatabaseInitializer.CreateContext())
            needTemplates = rows.Where(r => NeedsTemplates(ctx, r, jobId)).ToList();

        var ready = rows.Where(r => !needTemplates.Contains(r)).ToList();
        skipped = ready.Count > 0 ? Save(jobId, ready, releaseId) : new List<string>();
        return needTemplates;
    }

    /// <summary>
    /// Saves the rows to the job and returns the labels of any that were already on it (skipped).
    /// </summary>
    /// <param name="jobId">The job to add the hardware to.</param>
    /// <param name="rows">The rows to save.</param>
    /// <param name="releaseId">The job release to add to (null = base list).</param>
    public static List<string> Save(int jobId, IReadOnlyList<BulkHardwareRow> rows, int? releaseId)
    {
        var skipped = new List<string>();

        using var ctx = DatabaseInitializer.CreateContext();
        var hwRepo      = new HardwareItemRepository(ctx);
        var hitRepo     = new HardwareItemTemplateRepository(ctx);
        var jhRepo      = new JobHardwareRepository(ctx);
        var freqService = new FrequencyService(ctx);

        foreach (var row in rows)
        {
            int hwItemId;

            if (row.MatchedItem != null)
            {
                // Case A — matched; increment frequency and persist any remark the user set.
                hwItemId = row.MatchedItem.Id;
                var existing = ctx.HardwareItems.Find(hwItemId);
                if (existing != null)
                {
                    freqService.IncrementFrequency(existing);
                    if (!string.IsNullOrWhiteSpace(row.HardwareItemRemarks) &&
                        existing.Remarks != row.HardwareItemRemarks)
                    {
                        existing.Remarks = row.HardwareItemRemarks;
                        ctx.SaveChanges();
                    }
                }

                // Link any job-specific templates carried over from an import.
                foreach (var pending in row.PendingTemplates)
                {
                    int templateId;

                    // If the template was scoped to a different job, clone it for this job.
                    if (pending.Template.OriginJobId.HasValue && pending.Template.OriginJobId != jobId)
                    {
                        var existingClone = ctx.IndividualTemplates.FirstOrDefault(t =>
                            t.ManufacturerId == pending.Template.ManufacturerId &&
                            t.TemplateNumber == pending.Template.TemplateNumber &&
                            t.DoorMaterialId == pending.Template.DoorMaterialId &&
                            t.OriginJobId    == jobId);

                        if (existingClone != null)
                        {
                            templateId = existingClone.Id;
                        }
                        else
                        {
                            var clone = new IndividualTemplate
                            {
                                ManufacturerId    = pending.Template.ManufacturerId,
                                DescriptionId     = pending.Template.DescriptionId,
                                DoorMaterialId    = pending.Template.DoorMaterialId,
                                TemplateNumber    = pending.Template.TemplateNumber,
                                NumPages          = pending.Template.NumPages,
                                PagesToPrint      = pending.Template.PagesToPrint,
                                PagesToRotate     = pending.Template.PagesToRotate,
                                RotationDirection = pending.Template.RotationDirection,
                                OnlineLink        = pending.Template.OnlineLink,
                                LocalLink         = pending.Template.LocalLink,
                                OriginJobId       = jobId
                            };
                            ctx.IndividualTemplates.Add(clone);
                            ctx.SaveChanges();
                            templateId = clone.Id;
                        }
                    }
                    else
                    {
                        templateId = pending.Template.Id;
                    }

                    bool linkExists = ctx.HardwareItemTemplates.Any(hit =>
                        hit.HardwareItemId       == hwItemId &&
                        hit.IndividualTemplateId == templateId &&
                        hit.JobId                == jobId);

                    if (!linkExists)
                        hitRepo.Add(new HardwareItemTemplate
                        {
                            HardwareItemId       = hwItemId,
                            IndividualTemplateId = templateId,
                            JobId                = jobId
                        });
                }
            }
            else
            {
                // Case B — create new HardwareItem if not yet created.
                // hwRepo.Add() returns an existing record when a duplicate is found,
                // so explicitly update Remarks afterward to ensure the user's value is saved.
                if (row.CreatedItem == null)
                {
                    var newItem = hwRepo.Add(new HardwareItem
                    {
                        ManufacturerId = row.SelectedManufacturer!.Id,
                        DescriptionId  = row.SelectedDescription!.Id,
                        ModelNumber    = row.ModelNumber,
                        Remarks        = row.HardwareItemRemarks
                    });

                    if (!string.IsNullOrWhiteSpace(row.HardwareItemRemarks) &&
                        newItem.Remarks != row.HardwareItemRemarks)
                    {
                        var tracked = ctx.HardwareItems.Find(newItem.Id);
                        if (tracked != null)
                        {
                            tracked.Remarks = row.HardwareItemRemarks;
                            ctx.SaveChanges();
                        }
                    }

                    row.CreatedItem = newItem;
                }
                hwItemId = row.CreatedItem.Id;

                // Link staged templates to the new hardware item
                foreach (var pending in row.PendingTemplates)
                {
                    hitRepo.Add(new HardwareItemTemplate
                    {
                        HardwareItemId       = hwItemId,
                        IndividualTemplateId = pending.Template.Id,
                        JobId                = pending.IsJobSpecific ? jobId : null
                    });
                }
            }

            // Create one JobHardware record per label. Skip any that already exist.
            var labelsToSave = row.Labels.Count > 0
                ? row.Labels
                : new System.Collections.Generic.List<BulkHardwareLabel>
                    { new() { CustomLabel = "", Remarks = null } };

            foreach (var lbl in labelsToSave)
            {
                var customLabel = string.IsNullOrWhiteSpace(lbl.CustomLabel) ? null : lbl.CustomLabel;
                var remarks     = string.IsNullOrWhiteSpace(lbl.Remarks)     ? null : lbl.Remarks;

                bool isDuplicate = ctx.JobHardware.Any(jh =>
                    jh.JobId             == jobId &&
                    jh.HardwareItemId    == hwItemId &&
                    jh.CustomDescription == customLabel &&
                    jh.ReleaseId         == releaseId);

                if (isDuplicate)
                {
                    skipped.Add(row.ModelNumber + (customLabel != null ? $" ({customLabel})" : string.Empty));
                    continue;
                }

                jhRepo.Add(new JobHardware
                {
                    JobId             = jobId,
                    HardwareItemId    = hwItemId,
                    CustomDescription = customLabel,
                    CalloutRemarks    = remarks,
                    ReleaseId         = releaseId
                });
            }
        }

        return skipped;
    }
}
