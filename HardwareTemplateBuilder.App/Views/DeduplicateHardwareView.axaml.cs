using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Maintenance view that finds hardware items sharing the same Manufacturer + Description
/// + Model Number, presents each duplicate group for review, and merges the chosen
/// duplicates — re-linking all job and template references to the kept item.
/// </summary>
public partial class DeduplicateHardwareView : UserControl
{
    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    // ── Data models ────────────────────────────────────────────────────────────

    /// <summary>One hardware item with pre-computed usage counts for display.</summary>
    private sealed record ItemInfo(
        int Id,
        string ManufacturerName,
        string DescriptionText,
        string ModelNumber,
        int TemplateCount,
        int JobCount,
        int Frequency,
        string? Remarks)
    {
        /// <summary>Human-readable label shown in the per-group ListBox.</summary>
        public string DisplayLabel =>
            $"ID {Id}  │  {TemplateCount} template(s)  │  {JobCount} job link(s)  │  freq {Frequency}"
            + (string.IsNullOrWhiteSpace(Remarks) ? string.Empty : $"  \u2502  \"{Remarks}\"");
    }

    /// <summary>A group of hardware items that share Mfr + Description + ModelNumber.</summary>
    private sealed class DupGroup
    {
        public string ManufacturerName { get; init; } = string.Empty;
        public string DescriptionText  { get; init; } = string.Empty;
        public string ModelNumber      { get; init; } = string.Empty;
        public List<ItemInfo> Items    { get; init; } = new();

        /// <summary>The ListBox in the UI that lets the user select which item to keep.</summary>
        public ListBox? KeepListBox { get; set; }
    }

    private List<DupGroup> _groups = new();

    /// <summary>Initializes the deduplicate view.</summary>
    public DeduplicateHardwareView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            MainMenuButton.Click  += (_, _) => NavigationRequested?.Invoke("Dashboard");
            ScanButton.Click      += (_, _) => RunScan();
            MergeAllButton.Click  += async (_, _) => await MergeAllAsync();
        };
    }

    // ── Scan ───────────────────────────────────────────────────────────────────

    /// <summary>Queries the database for duplicate hardware items and rebuilds the results UI.</summary>
    private void RunScan()
    {
        StatusLabel.Text = string.Empty;
        GroupsPanel.Children.Clear();
        _groups.Clear();

        using var ctx = DatabaseInitializer.CreateContext();

        // Find sets of HardwareItem rows that share (ManufacturerId, DescriptionId, ModelNumber).
        var duplicateKeys = ctx.HardwareItems
            .GroupBy(h => new { h.ManufacturerId, h.DescriptionId, h.ModelNumber })
            .Where(g => g.Count() > 1)
            .Select(g => new { g.Key.ManufacturerId, g.Key.DescriptionId, g.Key.ModelNumber })
            .ToList();

        if (duplicateKeys.Count == 0)
        {
            StatusLabel.Text = "No duplicate hardware items found.";
            StatusLabel.Foreground = Brushes.DarkGreen;
            ResultsScroller.IsVisible = false;
            MergeAllButton.IsEnabled = false;
            return;
        }

        // For each duplicate key, load all matching items with usage counts.
        foreach (var key in duplicateKeys)
        {
            var items = ctx.HardwareItems
                .Include(h => h.Manufacturer)
                .Include(h => h.Description)
                .Where(h => h.ManufacturerId == key.ManufacturerId
                         && h.DescriptionId  == key.DescriptionId
                         && h.ModelNumber    == key.ModelNumber)
                .ToList();

            var mfrName  = items[0].Manufacturer?.ManufacturerName ?? "Unknown";
            var descText = items[0].Description?.DescriptionText   ?? "Unknown";

            var infoList = items.Select(h =>
            {
                var tplCount = ctx.HardwareItemTemplates.Count(t => t.HardwareItemId == h.Id);
                var jobCount = ctx.JobHardware.Count(jh => jh.HardwareItemId == h.Id);
                return new ItemInfo(h.Id, mfrName, descText, h.ModelNumber,
                                    tplCount, jobCount, h.Frequency, h.Remarks);
            }).ToList();

            var group = new DupGroup
            {
                ManufacturerName = mfrName,
                DescriptionText  = descText,
                ModelNumber      = key.ModelNumber,
                Items            = infoList
            };

            _groups.Add(group);
            GroupsPanel.Children.Add(BuildGroupPanel(group));
        }

        StatusLabel.Text = $"Found {_groups.Count} duplicate group(s). "
                         + "Select which item to keep in each group, then click Merge.";
        StatusLabel.Foreground = Brushes.DarkRed;
        ResultsScroller.IsVisible = true;
        MergeAllButton.IsEnabled  = true;
    }

    // ── UI builder ─────────────────────────────────────────────────────────────

    /// <summary>Builds the bordered panel for one duplicate group.</summary>
    private Border BuildGroupPanel(DupGroup group)
    {
        // Header label
        var header = new TextBlock
        {
            Text = $"{group.ManufacturerName}  —  {group.DescriptionText}  —  {group.ModelNumber}"
                 + $"  ({group.Items.Count} duplicates)",
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 6)
        };

        // Instruction
        var instruction = new TextBlock
        {
            Text = "Select the item to KEEP (all others will be merged into it then deleted):",
            FontSize = 11,
            Foreground = Brushes.DimGray,
            Margin = new Avalonia.Thickness(0, 0, 0, 4)
        };

        // ListBox: one entry per item
        var listBox = new ListBox
        {
            ItemsSource = group.Items,
            SelectionMode = SelectionMode.Single,
            Margin = new Avalonia.Thickness(0, 0, 0, 6)
        };
        listBox.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayLabel");
        // Default: select the item with the most job links, falling back to most templates.
        var suggested = group.Items
            .OrderByDescending(i => i.JobCount)
            .ThenByDescending(i => i.TemplateCount)
            .ThenByDescending(i => i.Frequency)
            .First();
        listBox.SelectedItem = suggested;

        group.KeepListBox = listBox;

        // Per-group Merge button
        var mergeBtn = new Button
        {
            Content = "Merge This Group",
            Padding = new Avalonia.Thickness(8, 3)
        };
        var groupRef = group;   // capture for closure
        mergeBtn.Click += async (_, _) =>
        {
            if (listBox.SelectedItem is not ItemInfo keep)
            {
                StatusLabel.Foreground = Brushes.DarkRed;
                StatusLabel.Text = $"Select an item to keep in group: {groupRef.ModelNumber}";
                return;
            }
            await MergeGroupAsync(groupRef, keep.Id);
        };

        var content = new StackPanel { Margin = new Avalonia.Thickness(8, 6) };
        content.Children.Add(header);
        content.Children.Add(instruction);
        content.Children.Add(listBox);
        content.Children.Add(mergeBtn);

        return new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(2),
            Margin = new Avalonia.Thickness(0, 0, 0, 8),
            Child = content
        };
    }

    // ── Merge logic ────────────────────────────────────────────────────────────

    /// <summary>
    /// Merges all groups at once, using whichever item is currently selected in each group's
    /// ListBox. Prompts for confirmation first.
    /// </summary>
    private async System.Threading.Tasks.Task MergeAllAsync()
    {
        // Validate that every group has a selection.
        var unselected = _groups.Where(g => g.KeepListBox?.SelectedItem == null).ToList();
        if (unselected.Count > 0)
        {
            StatusLabel.Foreground = Brushes.DarkRed;
            StatusLabel.Text = $"{unselected.Count} group(s) have no item selected. "
                             + "Select an item to keep in each group before merging all.";
            return;
        }

        var win = TopLevel.GetTopLevel(this) as Window;
        if (win == null) return;

        bool confirmed = await Helpers.DialogHelper.ConfirmAsync(win,
            $"This will merge {_groups.Count} duplicate group(s) and delete the discarded items.\n"
            + "All job links and templates will be re-pointed to the kept items.\n\n"
            + "Continue?",
            "Confirm Merge All");

        if (!confirmed) return;

        int merged = 0;
        foreach (var group in _groups.ToList())
        {
            if (group.KeepListBox?.SelectedItem is ItemInfo keep)
            {
                await MergeGroupAsync(group, keep.Id, suppressConfirm: true);
                merged++;
            }
        }

        StatusLabel.Foreground = Brushes.DarkGreen;
        StatusLabel.Text = $"Merged {merged} group(s). Run Scan again to verify.";
        MergeAllButton.IsEnabled = false;
    }

    /// <summary>
    /// Merges one duplicate group: re-links all job and template references from the
    /// discarded items to <paramref name="keepId"/>, then deletes the discarded items.
    /// </summary>
    private async System.Threading.Tasks.Task MergeGroupAsync(
        DupGroup group, int keepId, bool suppressConfirm = false)
    {
        if (!suppressConfirm)
        {
            var win = TopLevel.GetTopLevel(this) as Window;
            if (win == null) return;

            var discardIds = group.Items.Where(i => i.Id != keepId).Select(i => i.Id).ToList();
            bool confirmed = await Helpers.DialogHelper.ConfirmAsync(win,
                $"Keep item ID {keepId}.\n"
                + $"Discard and delete {discardIds.Count} item(s): IDs {string.Join(", ", discardIds)}.\n\n"
                + "All job links and templates will be re-pointed to the kept item. Continue?",
                "Confirm Merge");
            if (!confirmed) return;
        }

        using var ctx = DatabaseInitializer.CreateContext();

        var toDiscard = group.Items.Where(i => i.Id != keepId).ToList();

        foreach (var discard in toDiscard)
        {
            // 1. Re-link JobHardware rows to the kept item.
            var jobLinks = ctx.JobHardware.Where(jh => jh.HardwareItemId == discard.Id).ToList();
            foreach (var jh in jobLinks)
                jh.HardwareItemId = keepId;

            // 2. Re-link HardwareItemTemplates — only those not already on the kept item.
            var keptTemplateIds = ctx.HardwareItemTemplates
                .Where(t => t.HardwareItemId == keepId)
                .Select(t => t.IndividualTemplateId)
                .ToHashSet();

            var discardTemplates = ctx.HardwareItemTemplates
                .Where(t => t.HardwareItemId == discard.Id)
                .ToList();

            foreach (var hit in discardTemplates)
            {
                if (keptTemplateIds.Contains(hit.IndividualTemplateId))
                {
                    // Already linked to kept item — remove the duplicate link.
                    ctx.HardwareItemTemplates.Remove(hit);
                }
                else
                {
                    // Re-point to the kept item.
                    hit.HardwareItemId = keepId;
                    keptTemplateIds.Add(hit.IndividualTemplateId);
                }
            }

            ctx.SaveChanges();

            // 3. Delete the discarded HardwareItem.
            var entity = ctx.HardwareItems.Find(discard.Id);
            if (entity != null)
                ctx.HardwareItems.Remove(entity);
        }

        ctx.SaveChanges();

        // Remove the group's panel from the UI and from the list.
        var panelIndex = GroupsPanel.Children
            .OfType<Border>()
            .ToList()
            .IndexOf(GroupsPanel.Children
                .OfType<Border>()
                .Skip(_groups.IndexOf(group))
                .FirstOrDefault()!);

        // Simpler: rebuild the panel by rescanning instead of tracking border refs.
        // Just remove the group from the list and re-render remaining.
        _groups.Remove(group);
        RebuildGroupsPanel();

        if (!suppressConfirm)
        {
            StatusLabel.Foreground = Brushes.DarkGreen;
            StatusLabel.Text = $"Merged group '{group.ModelNumber}'. {_groups.Count} group(s) remaining.";
            MergeAllButton.IsEnabled = _groups.Count > 0;
        }
    }

    /// <summary>Clears and re-renders the groups panel from the current <see cref="_groups"/> list.</summary>
    private void RebuildGroupsPanel()
    {
        GroupsPanel.Children.Clear();
        foreach (var g in _groups)
        {
            // Re-create the panel; KeepListBox selection is reset — default suggestion re-applied.
            g.KeepListBox = null;
            GroupsPanel.Children.Add(BuildGroupPanel(g));
        }

        if (_groups.Count == 0)
        {
            ResultsScroller.IsVisible = false;
            MergeAllButton.IsEnabled  = false;
        }
    }
}
