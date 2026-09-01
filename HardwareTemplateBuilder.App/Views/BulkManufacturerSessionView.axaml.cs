using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// First screen of the bulk-add flow. Shows the list of manufacturers in the current session
/// and lets the user add new ones or drill into a manufacturer to enter hardware items.
/// "Add All to Job" commits the entire session to the job's hardware list.
/// </summary>
public partial class BulkManufacturerSessionView : UserControl
{
    private readonly int _jobId;
    private List<Manufacturer> _manufacturers = new();
    private BulkSessionDraft _draft = new();

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Initializes the session view for the given job.</summary>
    public BulkManufacturerSessionView(int jobId)
    {
        _jobId = jobId;
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        var job = ctx.Jobs.Find(_jobId);
        TitleLabel.Text = $"Bulk Hardware Entry — {job?.JobNumber ?? _jobId.ToString()}";

        _manufacturers = new ManufacturerRepository(ctx).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        MfrCombo.ItemsSource = _manufacturers;
        MfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");

        BackButton.Click           += (_, _) => NavigationRequested?.Invoke($"JobDetail:{_jobId}");
        AddMfrToSessionButton.Click += (_, _) => AddManufacturerToSession();
        ShowNewMfrButton.Click      += (_, _) => { NewMfrPanel.IsVisible = true; NewMfrBox.Focus(); };
        CancelNewMfrButton.Click    += (_, _) => { NewMfrPanel.IsVisible = false; NewMfrBox.Text = ""; };
        SaveNewMfrButton.Click      += (_, _) => SaveNewManufacturer();
        AddAllButton.Click          += async (_, _) => await AddAllToJobAsync();
        ItemsToFixButton.Click      += async (_, _) => await ShowItemsToFixAsync();

        LoadDraft();
        RebuildSessionPanel();
    }

    // ── Draft helpers ─────────────────────────────────────────────────────────

    private void LoadDraft()
    {
        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var draft = new BulkAddDraftRepository(ctx).GetByJob(_jobId);
            if (draft == null || string.IsNullOrWhiteSpace(draft.DraftJson) || draft.DraftJson == "[]")
                return;
            var parsed = JsonSerializer.Deserialize<BulkSessionDraft>(draft.DraftJson);
            if (parsed != null) _draft = parsed;
        }
        catch { /* corrupt draft — start fresh */ }
    }

    private void SaveDraft()
    {
        try
        {
            var json = JsonSerializer.Serialize(_draft);
            using var ctx = DatabaseInitializer.CreateContext();
            new BulkAddDraftRepository(ctx).Upsert(_jobId, json);
        }
        catch { }
    }

    // ── Session panel ─────────────────────────────────────────────────────────

    private void RebuildSessionPanel()
    {
        SessionPanel.Children.Clear();
        RefreshItemsToFixButton();

        if (_draft.Manufacturers.Count == 0)
        {
            SessionPanel.Children.Add(new TextBlock
            {
                Text = "No manufacturers added yet. Use the panel above to start.",
                Foreground = AppColors.Muted,
                Margin = new Avalonia.Thickness(4)
            });
            AddAllButton.IsEnabled = false;
            return;
        }

        foreach (var mfr in _draft.Manufacturers)
        {
            var mfrRef = mfr; // capture for closures

            var itemCount = mfr.Groups.Count;
            var calloutCount = mfr.Groups.Sum(g => Math.Max(g.Callouts.Count, 1));

            var nameLabel = new TextBlock
            {
                Text = mfr.ManufacturerName,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 200
            };
            var countLabel = new TextBlock
            {
                Text = itemCount == 0
                    ? "No items yet"
                    : $"{itemCount} item{(itemCount == 1 ? "" : "s")}, {calloutCount} line{(calloutCount == 1 ? "" : "s")}",
                Foreground = itemCount == 0 ? AppColors.Muted : AppColors.Success,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(12, 0)
            };

            var editBtn = new Button
            {
                Content = "Edit →",
                Margin = new Avalonia.Thickness(0, 0, 6, 0)
            };
            editBtn.Click += (_, _) =>
            {
                SaveDraft();
                NavigationRequested?.Invoke($"BulkHardwareEntry:{_jobId}:{mfrRef.ManufacturerId}");
            };

            var removeBtn = new Button
            {
                Content = "✕",
                Foreground = AppColors.Danger,
                Width = 28,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            removeBtn.Click += (_, _) =>
            {
                _draft.Manufacturers.Remove(mfrRef);
                SaveDraft();
                RebuildSessionPanel();
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Avalonia.Thickness(0, 2)
            };
            row.Children.Add(nameLabel);
            row.Children.Add(countLabel);
            row.Children.Add(editBtn);
            row.Children.Add(removeBtn);

            var border = new Border
            {
                BorderBrush = AppColors.Muted,
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(2),
                Padding = new Avalonia.Thickness(8, 6),
                Child = row
            };
            SessionPanel.Children.Add(border);
        }

        AddAllButton.IsEnabled = _draft.Manufacturers.Any(m => m.Groups.Any(g =>
            g.DescriptionId.HasValue && !string.IsNullOrWhiteSpace(g.ModelNumber)));
    }

    private void RefreshItemsToFixButton()
    {
        var fixCount = _draft.Manufacturers.Sum(m => m.Groups.Count(g => !string.IsNullOrEmpty(g.LastError)));
        ItemsToFixButton.IsEnabled = fixCount > 0;
        ItemsToFixButton.Content = fixCount > 0 ? $"Items to Fix ({fixCount})" : "Items to Fix";
    }

    // ── Add manufacturer to session ───────────────────────────────────────────

    private void AddManufacturerToSession()
    {
        if (MfrCombo.SelectedItem is not Manufacturer mfr)
        {
            StatusLabel.Text = "Select a manufacturer first.";
            return;
        }
        if (_draft.Manufacturers.Any(m => m.ManufacturerId == mfr.Id))
        {
            StatusLabel.Text = $"{mfr.ManufacturerName} is already in the session.";
            return;
        }
        _draft.Manufacturers.Add(new BulkSessionMfr
        {
            ManufacturerId = mfr.Id,
            ManufacturerName = mfr.ManufacturerName
        });
        SaveDraft();
        RebuildSessionPanel();
        StatusLabel.Text = "";
    }

    private void SaveNewManufacturer()
    {
        var name = NewMfrBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { StatusLabel.Text = "Enter a manufacturer name."; return; }

        using var ctx = DatabaseInitializer.CreateContext();
        var saved = new ManufacturerRepository(ctx).Add(new Manufacturer { ManufacturerName = name });

        _manufacturers = new ManufacturerRepository(ctx).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        MfrCombo.ItemsSource = _manufacturers;
        MfrCombo.SelectedItem = _manufacturers.FirstOrDefault(m => m.Id == saved.Id);

        NewMfrPanel.IsVisible = false;
        NewMfrBox.Text = "";
        StatusLabel.Text = $"Manufacturer \"{saved.ManufacturerName}\" added.";
    }

    // ── Commit: Add All to Job ────────────────────────────────────────────────

    private async Task AddAllToJobAsync()
    {
        var win = TopLevel.GetTopLevel(this) as Window;
        if (win != null)
        {
            bool confirmed = await DialogHelper.ConfirmAsync(win,
                "Add all hardware items from this session to the job?",
                "Confirm Add All");
            if (!confirmed) return;
        }

        int totalAdded = 0;
        var missingDescription = new List<string>();
        var templateNotFound   = new List<string>();
        var itemErrors         = new List<string>();
        var groupsToRemove     = new List<(BulkSessionMfr Mfr, BulkSessionGroup Group)>();

        using var ctx = DatabaseInitializer.CreateContext();

        foreach (var mfr in _draft.Manufacturers)
        {
            foreach (var group in mfr.Groups)
            {
                var label = string.IsNullOrWhiteSpace(group.ModelNumber) ? "(blank model number)" : group.ModelNumber;

                if (!group.DescriptionId.HasValue || string.IsNullOrWhiteSpace(group.ModelNumber))
                {
                    group.LastError = "No description selected — pick a description for this row and re-add it.";
                    missingDescription.Add($"{mfr.ManufacturerName} / {label}: {group.LastError}");
                    continue;
                }

                // Each item is committed in its own transaction so a failure partway through
                // (e.g. an unexpected constraint violation) rolls back cleanly and only that
                // item is skipped, instead of aborting the whole session or leaving partial rows.
                using var tx = ctx.Database.BeginTransaction();
                try
                {
                    int groupAdded = 0;

                    // Find or create the HardwareItem. Matches on Manufacturer+Description+ModelNumber
                    // only (mirrors HardwareItemRepository.FindDuplicate), regardless of IsActive, so a
                    // previously-deactivated item is reused rather than duplicated as a new active row.
                    var item = ctx.HardwareItems.FirstOrDefault(h =>
                        h.ManufacturerId == mfr.ManufacturerId &&
                        h.DescriptionId  == group.DescriptionId.Value &&
                        h.ModelNumber    == group.ModelNumber);

                    if (item == null)
                    {
                        item = new HardwareItem
                        {
                            ManufacturerId = mfr.ManufacturerId,
                            DescriptionId  = group.DescriptionId.Value,
                            ModelNumber    = group.ModelNumber,
                            Remarks        = group.HardwareItemRemarks
                        };
                        ctx.HardwareItems.Add(item);
                        ctx.SaveChanges();
                    }
                    else if (!string.IsNullOrWhiteSpace(group.HardwareItemRemarks) &&
                             item.Remarks != group.HardwareItemRemarks)
                    {
                        // Update remarks if provided and different from existing
                        item.Remarks = group.HardwareItemRemarks;
                        ctx.SaveChanges();
                    }

                    // Link the template if specified and not already linked.
                    if (!string.IsNullOrWhiteSpace(group.TemplateNumber))
                    {
                        // Bulk-add doesn't capture door material, so a Manufacturer+TemplateNumber
                        // match could be ambiguous if multiple door-material variants share the
                        // same number — surface that instead of silently linking an arbitrary one.
                        var matches = ctx.IndividualTemplates.Where(t =>
                            t.ManufacturerId  == mfr.ManufacturerId &&
                            t.TemplateNumber  == group.TemplateNumber).ToList();

                        if (matches.Count > 1)
                        {
                            templateNotFound.Add($"{mfr.ManufacturerName} / {group.ModelNumber}: template '{group.TemplateNumber}' matches {matches.Count} door-material variants — link it manually on the Individual Templates page.");
                        }
                        else if (matches.Count == 1)
                        {
                            var template = matches[0];

                            // Matches HardwareItemTemplateRepository.FindDuplicate's real key
                            // (HardwareItemId + IndividualTemplateId + JobId). This path always
                            // creates a global link (JobId left null), so the "already linked"
                            // check must also require JobId == null — otherwise an existing
                            // job-scoped link for the same pair would cause the global link to be
                            // silently skipped even though it doesn't actually exist yet.
                            bool linked = ctx.HardwareItemTemplates.Any(hit =>
                                hit.HardwareItemId       == item.Id &&
                                hit.IndividualTemplateId == template.Id &&
                                hit.JobId                == null);
                            if (!linked)
                            {
                                ctx.HardwareItemTemplates.Add(new HardwareItemTemplate
                                {
                                    HardwareItemId       = item.Id,
                                    IndividualTemplateId = template.Id
                                });
                                ctx.SaveChanges();
                            }
                        }
                        else
                        {
                            templateNotFound.Add($"{mfr.ManufacturerName} / {group.ModelNumber}: template '{group.TemplateNumber}' not found");
                        }
                    }

                    // Create one JobHardware row per callout (or one row with no label if no callouts).
                    var callouts = group.Callouts.Count > 0
                        ? group.Callouts
                        : new List<BulkSessionCallout> { new BulkSessionCallout() };

                    foreach (var callout in callouts)
                    {
                        var calloutLabel = string.IsNullOrWhiteSpace(callout.Label) ? null : callout.Label.Trim();
                        var already = ctx.JobHardware.Any(jh =>
                            jh.JobId          == _jobId &&
                            jh.HardwareItemId == item.Id &&
                            jh.CustomDescription == calloutLabel &&
                            jh.ReleaseId      == BulkAddSession.ReleaseId);
                        if (already) continue;

                        ctx.JobHardware.Add(new JobHardware
                        {
                            JobId             = _jobId,
                            HardwareItemId    = item.Id,
                            CustomDescription = calloutLabel,
                            CalloutRemarks    = string.IsNullOrWhiteSpace(callout.CalloutRemarks)
                                                   ? null : callout.CalloutRemarks.Trim(),
                            ReleaseId         = BulkAddSession.ReleaseId
                        });
                        ctx.SaveChanges();
                        groupAdded++;
                    }

                    tx.Commit();
                    totalAdded += groupAdded;

                    // Fully committed — nothing left to retry, so drop it from the draft.
                    group.LastError = null;
                    groupsToRemove.Add((mfr, group));
                }
                catch (Exception ex)
                {
                    tx.Rollback();
                    ctx.ChangeTracker.Clear();
                    group.LastError =
                        $"{ex.Message} — check the description, model number, and template, then try again.";
                    itemErrors.Add($"{mfr.ManufacturerName} / {label}: {group.LastError}");
                }
            }
        }

        // Remove only the items that were fully committed; anything still needing a fix
        // (missing description, or an error) stays in the draft for "Items to Fix".
        foreach (var (mfr, group) in groupsToRemove)
            mfr.Groups.Remove(group);
        _draft.Manufacturers.RemoveAll(m => m.Groups.Count == 0);

        if (_draft.Manufacturers.Count == 0)
            new BulkAddDraftRepository(ctx).DeleteByJob(_jobId);
        else
            SaveDraft();

        RebuildSessionPanel();

        var msg = $"Added {totalAdded} line item(s) to the job.";

        void AppendSection(string title, List<string> lines)
        {
            if (lines.Count == 0) return;
            msg += $"\n\n{title} ({lines.Count}):\n" + string.Join("\n", lines.Select(s => $"  • {s}"));
        }

        AppendSection("Skipped — missing description", missingDescription);
        AppendSection("Note — template not found (link manually)", templateNotFound);
        AppendSection("Skipped — error", itemErrors);

        bool hasItemsToFix = missingDescription.Count > 0 || itemErrors.Count > 0;
        if (hasItemsToFix)
            msg += "\n\nSkipped items remain in this session — click \"Items to Fix\" to review and correct them.";

        if (win != null)
            await DialogHelper.ShowInfoAsync(win, msg, "Session Complete");
        else
            StatusLabel.Text = msg;

        // Stay on this screen if there's anything left to fix so the user can act on it
        // immediately; otherwise the session is fully committed, so return to the job.
        if (!hasItemsToFix)
            NavigationRequested?.Invoke($"JobDetail:{_jobId}");
    }

    /// <summary>
    /// Shows the list of hardware items skipped during the last "Add All to Job" run
    /// (missing description or an unexpected error), letting the user jump straight to the
    /// relevant manufacturer's entry screen to fix and re-add one.
    /// </summary>
    private async Task ShowItemsToFixAsync()
    {
        var win = TopLevel.GetTopLevel(this) as Window;
        if (win == null) return;

        var rows = _draft.Manufacturers
            .SelectMany(m => m.Groups
                .Where(g => !string.IsNullOrEmpty(g.LastError))
                .Select(g => new FixItemRow
                {
                    ManufacturerId   = m.ManufacturerId,
                    ManufacturerName = m.ManufacturerName,
                    ModelNumber      = string.IsNullOrWhiteSpace(g.ModelNumber) ? "(blank model number)" : g.ModelNumber,
                    Reason           = g.LastError!
                }))
            .ToList();

        if (rows.Count == 0)
        {
            await DialogHelper.ShowInfoAsync(win, "No items currently need fixing.", "Items to Fix");
            return;
        }

        var dialog = new ItemsToFixDialog(rows);
        var manufacturerId = await dialog.ShowDialog<int?>(win);
        if (manufacturerId.HasValue)
        {
            SaveDraft();
            NavigationRequested?.Invoke($"BulkHardwareEntry:{_jobId}:{manufacturerId.Value}");
        }
    }
}
