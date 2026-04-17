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
/// Maintenance view with two scan-and-merge tools:
/// (1) hardware items sharing the same Manufacturer + Description + Model Number,
/// (2) templates sharing the same Online Link or Local File path.
/// </summary>
public partial class DeduplicateHardwareView : UserControl
{
    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    // ── Hardware duplicate models ──────────────────────────────────────────────

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
        public string DisplayLabel =>
            $"ID {Id}  │  {TemplateCount} template(s)  │  {JobCount} job link(s)  │  freq {Frequency}"
            + (string.IsNullOrWhiteSpace(Remarks) ? string.Empty : $"  \u2502  \"{Remarks}\"");
    }

    private sealed class DupGroup
    {
        public string ManufacturerName { get; init; } = string.Empty;
        public string DescriptionText  { get; init; } = string.Empty;
        public string ModelNumber      { get; init; } = string.Empty;
        public List<ItemInfo> Items    { get; init; } = new();
        public ListBox? KeepListBox    { get; set; }
    }

    private List<DupGroup> _groups = new();

    // ── Template duplicate models ──────────────────────────────────────────────

    /// <summary>One template with pre-computed usage counts for display.</summary>
    private sealed record TplInfo(
        int Id,
        string ManufacturerName,
        string TemplateNumber,
        string DescriptionText,
        string PagesToPrint,
        int HardwareCount,
        string? OnlineLink,
        string? LocalLink)
    {
        public string DisplayLabel =>
            $"ID {Id}  │  {ManufacturerName}  │  #{TemplateNumber}  │  {HardwareCount} hw item(s)"
            + (string.IsNullOrWhiteSpace(OnlineLink) ? string.Empty : $"  │  {OnlineLink}")
            + (string.IsNullOrWhiteSpace(LocalLink)  ? string.Empty : $"  │  {LocalLink}");
    }

    /// <summary>A group of templates that share a link (online or local).</summary>
    private sealed class TplDupGroup
    {
        public string SharedLink      { get; init; } = string.Empty;
        public string LinkType        { get; init; } = string.Empty;  // "Online" or "Local"
        public List<TplInfo> Items    { get; init; } = new();
        public ListBox? KeepListBox   { get; set; }
    }

    private List<TplDupGroup> _tplGroups = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>Initializes the deduplicate view.</summary>
    public DeduplicateHardwareView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ScanButton.Click              += (_, _) => RunHardwareScan();
            MergeAllButton.Click          += async (_, _) => await MergeAllHardwareAsync();
            ScanTemplatesButton.Click     += (_, _) => RunTemplateScan();
            MergeAllTemplatesButton.Click += async (_, _) => await MergeAllTemplatesAsync();
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Hardware scan + merge
    // ══════════════════════════════════════════════════════════════════════════

    private void RunHardwareScan()
    {
        StatusLabel.Text = string.Empty;
        GroupsPanel.Children.Clear();
        _groups.Clear();

        using var ctx = DatabaseInitializer.CreateContext();

        var duplicateKeys = ctx.HardwareItems
            .GroupBy(h => new { h.ManufacturerId, h.DescriptionId, h.ModelNumber })
            .Where(g => g.Count() > 1)
            .Select(g => new { g.Key.ManufacturerId, g.Key.DescriptionId, g.Key.ModelNumber })
            .ToList();

        if (duplicateKeys.Count == 0)
        {
            StatusLabel.Text       = "No duplicate hardware items found.";
            StatusLabel.Foreground = Brushes.DarkGreen;
            MergeAllButton.IsEnabled = false;
            return;
        }

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
            GroupsPanel.Children.Add(BuildHardwareGroupPanel(group));
        }

        StatusLabel.Text       = $"Found {_groups.Count} duplicate hardware group(s). Select which item to keep, then click Merge.";
        StatusLabel.Foreground = Brushes.DarkRed;
        MergeAllButton.IsEnabled = true;
    }

    private Border BuildHardwareGroupPanel(DupGroup group)
    {
        var header = new TextBlock
        {
            Text = $"{group.ManufacturerName}  —  {group.DescriptionText}  —  {group.ModelNumber}"
                 + $"  ({group.Items.Count} duplicates)",
            FontWeight   = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Avalonia.Thickness(0, 0, 0, 6)
        };

        var instruction = new TextBlock
        {
            Text       = "Select the item to KEEP (all others will be merged into it then deleted):",
            FontSize   = 11,
            Foreground = Brushes.DimGray,
            Margin     = new Avalonia.Thickness(0, 0, 0, 4)
        };

        var listBox = new ListBox
        {
            ItemsSource   = group.Items,
            SelectionMode = SelectionMode.Single,
            Margin        = new Avalonia.Thickness(0, 0, 0, 6)
        };
        listBox.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayLabel");

        var suggested = group.Items
            .OrderByDescending(i => i.JobCount)
            .ThenByDescending(i => i.TemplateCount)
            .ThenByDescending(i => i.Frequency)
            .First();
        listBox.SelectedItem  = suggested;
        group.KeepListBox     = listBox;

        var mergeBtn = new Button
        {
            Content = "Merge This Group",
            Padding = new Avalonia.Thickness(8, 3)
        };
        var groupRef = group;
        mergeBtn.Click += async (_, _) =>
        {
            if (listBox.SelectedItem is not ItemInfo keep)
            {
                StatusLabel.Foreground = Brushes.DarkRed;
                StatusLabel.Text = $"Select an item to keep in group: {groupRef.ModelNumber}";
                return;
            }
            await MergeHardwareGroupAsync(groupRef, keep.Id);
        };

        var content = new StackPanel { Margin = new Avalonia.Thickness(8, 6) };
        content.Children.Add(header);
        content.Children.Add(instruction);
        content.Children.Add(listBox);
        content.Children.Add(mergeBtn);

        return new Border
        {
            BorderBrush     = Brushes.Gray,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius    = new Avalonia.CornerRadius(2),
            Margin          = new Avalonia.Thickness(0, 0, 0, 8),
            Child           = content
        };
    }

    private async System.Threading.Tasks.Task MergeAllHardwareAsync()
    {
        var unselected = _groups.Where(g => g.KeepListBox?.SelectedItem == null).ToList();
        if (unselected.Count > 0)
        {
            StatusLabel.Foreground = Brushes.DarkRed;
            StatusLabel.Text = $"{unselected.Count} hardware group(s) have no item selected.";
            return;
        }

        var win = TopLevel.GetTopLevel(this) as Window;
        if (win == null) return;

        bool confirmed = await Helpers.DialogHelper.ConfirmAsync(win,
            $"Merge {_groups.Count} hardware duplicate group(s)?\n"
            + "All job links and templates will be re-pointed to the kept items.",
            "Confirm Merge All Hardware");
        if (!confirmed) return;

        int merged = 0;
        foreach (var group in _groups.ToList())
        {
            if (group.KeepListBox?.SelectedItem is ItemInfo keep)
            {
                await MergeHardwareGroupAsync(group, keep.Id, suppressConfirm: true);
                merged++;
            }
        }

        StatusLabel.Foreground   = Brushes.DarkGreen;
        StatusLabel.Text         = $"Merged {merged} hardware group(s). Run Scan again to verify.";
        MergeAllButton.IsEnabled = false;
    }

    private async System.Threading.Tasks.Task MergeHardwareGroupAsync(
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
            // Re-link JobHardware rows.
            var jobLinks = ctx.JobHardware.Where(jh => jh.HardwareItemId == discard.Id).ToList();
            foreach (var jh in jobLinks)
                jh.HardwareItemId = keepId;

            // Re-link HardwareItemTemplates (deduplicating).
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
                    ctx.HardwareItemTemplates.Remove(hit);
                else
                {
                    hit.HardwareItemId = keepId;
                    keptTemplateIds.Add(hit.IndividualTemplateId);
                }
            }

            ctx.SaveChanges();

            var entity = ctx.HardwareItems.Find(discard.Id);
            if (entity != null)
                ctx.HardwareItems.Remove(entity);
        }

        ctx.SaveChanges();

        _groups.Remove(group);
        RebuildHardwarePanel();

        if (!suppressConfirm)
        {
            StatusLabel.Foreground   = Brushes.DarkGreen;
            StatusLabel.Text         = $"Merged hardware group '{group.ModelNumber}'. {_groups.Count} group(s) remaining.";
            MergeAllButton.IsEnabled = _groups.Count > 0;
        }
    }

    private void RebuildHardwarePanel()
    {
        GroupsPanel.Children.Clear();
        foreach (var g in _groups)
        {
            g.KeepListBox = null;
            GroupsPanel.Children.Add(BuildHardwareGroupPanel(g));
        }
        if (_groups.Count == 0)
            MergeAllButton.IsEnabled = false;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Template scan + merge
    // ══════════════════════════════════════════════════════════════════════════

    private void RunTemplateScan()
    {
        StatusLabel.Text = string.Empty;
        TplGroupsPanel.Children.Clear();
        _tplGroups.Clear();

        using var ctx = DatabaseInitializer.CreateContext();

        var allTemplates = ctx.IndividualTemplates
            .Include(t => t.Manufacturer)
            .Include(t => t.Description)
            .ToList();

        // Build TplInfo for each template (hardware item count pre-computed).
        var hwCountByTemplate = ctx.HardwareItemTemplates
            .GroupBy(hit => hit.IndividualTemplateId)
            .ToDictionary(g => g.Key, g => g.Count());

        TplInfo MakeInfo(IndividualTemplate t) => new(
            t.Id,
            t.Manufacturer?.ManufacturerName ?? "Unknown",
            t.TemplateNumber,
            t.Description?.DescriptionText ?? "Unknown",
            t.PagesToPrint,
            hwCountByTemplate.GetValueOrDefault(t.Id, 0),
            t.OnlineLink,
            t.LocalLink);

        // Group by non-empty OnlineLink.
        var byOnline = allTemplates
            .Where(t => !string.IsNullOrWhiteSpace(t.OnlineLink))
            .GroupBy(t => t.OnlineLink!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        // Group by non-empty LocalLink.
        var byLocal = allTemplates
            .Where(t => !string.IsNullOrWhiteSpace(t.LocalLink))
            .GroupBy(t => t.LocalLink!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        // Track which template IDs have already been placed in a group to avoid showing
        // the same template twice when both its links happen to match other templates.
        var placedIds = new HashSet<int>();

        foreach (var g in byOnline)
        {
            var items = g.Select(MakeInfo).ToList();
            var group = new TplDupGroup
            {
                SharedLink = g.Key,
                LinkType   = "Online",
                Items      = items
            };
            _tplGroups.Add(group);
            TplGroupsPanel.Children.Add(BuildTemplateGroupPanel(group));
            foreach (var i in items) placedIds.Add(i.Id);
        }

        foreach (var g in byLocal)
        {
            // Skip if all members already appear in an online-link group.
            var newItems = g.Where(t => !placedIds.Contains(t.Id)).ToList();
            if (newItems.Count < 2) continue;

            var items = newItems.Select(MakeInfo).ToList();
            var group = new TplDupGroup
            {
                SharedLink = g.Key,
                LinkType   = "Local",
                Items      = items
            };
            _tplGroups.Add(group);
            TplGroupsPanel.Children.Add(BuildTemplateGroupPanel(group));
            foreach (var i in items) placedIds.Add(i.Id);
        }

        if (_tplGroups.Count == 0)
        {
            StatusLabel.Text       = "No duplicate templates found.";
            StatusLabel.Foreground = Brushes.DarkGreen;
            MergeAllTemplatesButton.IsEnabled = false;
            return;
        }

        StatusLabel.Text       = $"Found {_tplGroups.Count} duplicate template group(s). Select which template to keep, then click Merge.";
        StatusLabel.Foreground = Brushes.DarkRed;
        MergeAllTemplatesButton.IsEnabled = true;
    }

    private Border BuildTemplateGroupPanel(TplDupGroup group)
    {
        var linkLabel = new TextBlock
        {
            Text         = $"{group.LinkType} link: {group.SharedLink}",
            FontWeight   = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Avalonia.Thickness(0, 0, 0, 4)
        };

        var countLabel = new TextBlock
        {
            Text       = $"{group.Items.Count} templates share this link:",
            FontSize   = 11,
            Foreground = Brushes.DimGray,
            Margin     = new Avalonia.Thickness(0, 0, 0, 4)
        };

        var instruction = new TextBlock
        {
            Text       = "Select the template to KEEP (others will be merged into it then deleted):",
            FontSize   = 11,
            Foreground = Brushes.DimGray,
            Margin     = new Avalonia.Thickness(0, 0, 0, 4)
        };

        var listBox = new ListBox
        {
            ItemsSource   = group.Items,
            SelectionMode = SelectionMode.Single,
            Margin        = new Avalonia.Thickness(0, 0, 0, 6)
        };
        listBox.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayLabel");

        // Default selection: the one with the most hardware item links.
        var suggested = group.Items
            .OrderByDescending(i => i.HardwareCount)
            .First();
        listBox.SelectedItem = suggested;
        group.KeepListBox    = listBox;

        var mergeBtn = new Button
        {
            Content = "Merge This Group",
            Padding = new Avalonia.Thickness(8, 3)
        };
        var groupRef = group;
        mergeBtn.Click += async (_, _) =>
        {
            if (listBox.SelectedItem is not TplInfo keep)
            {
                StatusLabel.Foreground = Brushes.DarkRed;
                StatusLabel.Text = "Select a template to keep.";
                return;
            }
            await MergeTemplateGroupAsync(groupRef, keep.Id);
        };

        var content = new StackPanel { Margin = new Avalonia.Thickness(8, 6) };
        content.Children.Add(linkLabel);
        content.Children.Add(countLabel);
        content.Children.Add(instruction);
        content.Children.Add(listBox);
        content.Children.Add(mergeBtn);

        return new Border
        {
            BorderBrush     = Brushes.Gray,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius    = new Avalonia.CornerRadius(2),
            Margin          = new Avalonia.Thickness(0, 0, 0, 8),
            Child           = content
        };
    }

    private async System.Threading.Tasks.Task MergeAllTemplatesAsync()
    {
        var unselected = _tplGroups.Where(g => g.KeepListBox?.SelectedItem == null).ToList();
        if (unselected.Count > 0)
        {
            StatusLabel.Foreground = Brushes.DarkRed;
            StatusLabel.Text = $"{unselected.Count} template group(s) have no item selected.";
            return;
        }

        var win = TopLevel.GetTopLevel(this) as Window;
        if (win == null) return;

        bool confirmed = await Helpers.DialogHelper.ConfirmAsync(win,
            $"Merge {_tplGroups.Count} template duplicate group(s)?\n"
            + "All hardware item links will be re-pointed to the kept templates.",
            "Confirm Merge All Templates");
        if (!confirmed) return;

        int merged = 0;
        foreach (var group in _tplGroups.ToList())
        {
            if (group.KeepListBox?.SelectedItem is TplInfo keep)
            {
                await MergeTemplateGroupAsync(group, keep.Id, suppressConfirm: true);
                merged++;
            }
        }

        StatusLabel.Foreground            = Brushes.DarkGreen;
        StatusLabel.Text                  = $"Merged {merged} template group(s). Run Scan again to verify.";
        MergeAllTemplatesButton.IsEnabled = false;
    }

    /// <summary>
    /// Merges one template duplicate group: re-links all HardwareItemTemplate and
    /// JobTemplateSnapshot rows from discarded templates to <paramref name="keepId"/>,
    /// then deletes the discarded templates.
    /// </summary>
    private async System.Threading.Tasks.Task MergeTemplateGroupAsync(
        TplDupGroup group, int keepId, bool suppressConfirm = false)
    {
        if (!suppressConfirm)
        {
            var win = TopLevel.GetTopLevel(this) as Window;
            if (win == null) return;

            var discardIds = group.Items.Where(i => i.Id != keepId).Select(i => i.Id).ToList();
            bool confirmed = await Helpers.DialogHelper.ConfirmAsync(win,
                $"Keep template ID {keepId}.\n"
                + $"Discard and delete {discardIds.Count} template(s): IDs {string.Join(", ", discardIds)}.\n\n"
                + "All hardware item links will be re-pointed to the kept template. Continue?",
                "Confirm Template Merge");
            if (!confirmed) return;
        }

        using var ctx = DatabaseInitializer.CreateContext();
        var toDiscard = group.Items.Where(i => i.Id != keepId).ToList();

        foreach (var discard in toDiscard)
        {
            // Re-link HardwareItemTemplates (deduplicating).
            var keptHwIds = ctx.HardwareItemTemplates
                .Where(hit => hit.IndividualTemplateId == keepId)
                .Select(hit => hit.HardwareItemId)
                .ToHashSet();

            var discardHits = ctx.HardwareItemTemplates
                .Where(hit => hit.IndividualTemplateId == discard.Id)
                .ToList();

            foreach (var hit in discardHits)
            {
                if (keptHwIds.Contains(hit.HardwareItemId))
                    ctx.HardwareItemTemplates.Remove(hit);
                else
                {
                    hit.IndividualTemplateId = keepId;
                    keptHwIds.Add(hit.HardwareItemId);
                }
            }

            // Re-link JobTemplateSnapshot rows (historical, but FK is Restrict so must move them).
            var snapshots = ctx.JobTemplateSnapshots
                .Where(s => s.IndividualTemplateId == discard.Id)
                .ToList();
            foreach (var s in snapshots)
                s.IndividualTemplateId = keepId;

            ctx.SaveChanges();

            // Now safe to delete the discarded template.
            var entity = ctx.IndividualTemplates.Find(discard.Id);
            if (entity != null)
                ctx.IndividualTemplates.Remove(entity);

            ctx.SaveChanges();
        }

        _tplGroups.Remove(group);
        RebuildTemplatePanel();

        if (!suppressConfirm)
        {
            StatusLabel.Foreground            = Brushes.DarkGreen;
            StatusLabel.Text                  = $"Merged template group. {_tplGroups.Count} group(s) remaining.";
            MergeAllTemplatesButton.IsEnabled = _tplGroups.Count > 0;
        }
    }

    private void RebuildTemplatePanel()
    {
        TplGroupsPanel.Children.Clear();
        foreach (var g in _tplGroups)
        {
            g.KeepListBox = null;
            TplGroupsPanel.Children.Add(BuildTemplateGroupPanel(g));
        }
        if (_tplGroups.Count == 0)
            MergeAllTemplatesButton.IsEnabled = false;
    }
}
