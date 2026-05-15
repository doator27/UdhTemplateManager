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

        if (_draft.Manufacturers.Count == 0)
        {
            SessionPanel.Children.Add(new TextBlock
            {
                Text = "No manufacturers added yet. Use the panel above to start.",
                Foreground = Brushes.Gray,
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
                Foreground = itemCount == 0 ? Brushes.Gray : Brushes.DarkGreen,
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
                Foreground = Brushes.DarkRed,
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
                BorderBrush = Brushes.Gray,
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
        var skipped = new List<string>();

        using var ctx = DatabaseInitializer.CreateContext();

        foreach (var mfr in _draft.Manufacturers)
        {
            foreach (var group in mfr.Groups)
            {
                if (!group.DescriptionId.HasValue || string.IsNullOrWhiteSpace(group.ModelNumber))
                    continue;

                // Find or create the HardwareItem.
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
                    var template = ctx.IndividualTemplates.FirstOrDefault(t =>
                        t.ManufacturerId  == mfr.ManufacturerId &&
                        t.TemplateNumber  == group.TemplateNumber);

                    if (template != null)
                    {
                        bool linked = ctx.HardwareItemTemplates.Any(hit =>
                            hit.HardwareItemId       == item.Id &&
                            hit.IndividualTemplateId == template.Id);
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
                        skipped.Add($"{mfr.ManufacturerName} / {group.ModelNumber}: template '{group.TemplateNumber}' not found");
                    }
                }

                // Create one JobHardware row per callout (or one row with no label if no callouts).
                var callouts = group.Callouts.Count > 0
                    ? group.Callouts
                    : new List<BulkSessionCallout> { new BulkSessionCallout() };

                foreach (var callout in callouts)
                {
                    var label = string.IsNullOrWhiteSpace(callout.Label) ? null : callout.Label.Trim();
                    var already = ctx.JobHardware.Any(jh =>
                        jh.JobId          == _jobId &&
                        jh.HardwareItemId == item.Id &&
                        jh.CustomDescription == label);
                    if (already) continue;

                    ctx.JobHardware.Add(new JobHardware
                    {
                        JobId             = _jobId,
                        HardwareItemId    = item.Id,
                        CustomDescription = label,
                        CalloutRemarks    = string.IsNullOrWhiteSpace(callout.CalloutRemarks)
                                               ? null : callout.CalloutRemarks.Trim()
                    });
                    ctx.SaveChanges();
                    totalAdded++;
                }
            }
        }

        // Clear the draft once committed.
        new BulkAddDraftRepository(ctx).DeleteByJob(_jobId);
        _draft = new BulkSessionDraft();
        RebuildSessionPanel();

        var msg = $"Added {totalAdded} line item(s) to the job.";
        if (skipped.Count > 0)
            msg += $"\n\nNote — {skipped.Count} template(s) not found (link manually):\n" +
                   string.Join("\n", skipped.Select(s => $"  • {s}"));

        if (win != null)
            await DialogHelper.ShowInfoAsync(win, msg, "Session Complete");
        else
            StatusLabel.Text = msg;

        NavigationRequested?.Invoke($"JobDetail:{_jobId}");
    }
}
