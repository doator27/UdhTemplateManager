using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Maintenance view with a scan-and-merge tool for templates that share the same
/// Online Link or Local File path.
/// </summary>
public partial class DeduplicateHardwareView : UserControl
{
    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

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
            ScanTemplatesButton.Click     += (_, _) => RunTemplateScan();
            MergeAllTemplatesButton.Click += async (_, _) => await MergeAllTemplatesAsync();
        };
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

        // Load all ignored duplicates to filter them out.
        var ignoredDuplicates = ctx.IgnoredTemplateDuplicates
            .Select(i => new { i.SharedLink, i.LinkType })
            .ToHashSet();

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
            .Where(g => g.Count() > 1)
            .Where(g => !ignoredDuplicates.Contains(new { SharedLink = g.Key, LinkType = "Online" }));

        // Group by non-empty LocalLink.
        var byLocal = allTemplates
            .Where(t => !string.IsNullOrWhiteSpace(t.LocalLink))
            .GroupBy(t => t.LocalLink!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Where(g => !ignoredDuplicates.Contains(new { SharedLink = g.Key, LinkType = "Local" }));

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
            StatusLabel.Foreground = AppColors.Success;
            MergeAllTemplatesButton.IsEnabled = false;
            return;
        }

        StatusLabel.Text       = $"Found {_tplGroups.Count} duplicate template group(s). Select which template to keep, then click Merge.";
        StatusLabel.Foreground = AppColors.Danger;
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
            Foreground = AppColors.Muted,
            Margin     = new Avalonia.Thickness(0, 0, 0, 4)
        };

        var instruction = new TextBlock
        {
            Text       = "Select the template to KEEP (others will be merged into it then deleted):",
            FontSize   = 11,
            Foreground = AppColors.Muted,
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
                StatusLabel.Foreground = AppColors.Danger;
                StatusLabel.Text = "Select a template to keep.";
                return;
            }
            await MergeTemplateGroupAsync(groupRef, keep.Id);
        };

        var keepBothBtn = new Button
        {
            Content = "Keep Both (Ignore)",
            Padding = new Avalonia.Thickness(8, 3),
            Margin  = new Avalonia.Thickness(8, 0, 0, 0)
        };
        keepBothBtn.Click += async (_, _) => await KeepBothTemplatesAsync(groupRef);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0
        };
        buttonPanel.Children.Add(mergeBtn);
        buttonPanel.Children.Add(keepBothBtn);

        var content = new StackPanel { Margin = new Avalonia.Thickness(8, 6) };
        content.Children.Add(linkLabel);
        content.Children.Add(countLabel);
        content.Children.Add(instruction);
        content.Children.Add(listBox);
        content.Children.Add(buttonPanel);

        return new Border
        {
            BorderBrush     = AppColors.Muted,
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
            StatusLabel.Foreground = AppColors.Danger;
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

        StatusLabel.Foreground            = AppColors.Success;
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
            StatusLabel.Foreground            = AppColors.Success;
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

    /// <summary>
    /// Marks a template duplicate group as "ignored" so it won't appear in future scans.
    /// This allows users to keep templates with the same link but different page ranges.
    /// </summary>
    private async System.Threading.Tasks.Task KeepBothTemplatesAsync(TplDupGroup group)
    {
        var win = TopLevel.GetTopLevel(this) as Window;
        if (win == null) return;

        bool confirmed = await Helpers.DialogHelper.ConfirmAsync(win,
            $"Mark this duplicate as 'ignored'?\n\n"
            + $"Link: {group.SharedLink}\n"
            + $"Type: {group.LinkType}\n\n"
            + "This duplicate group will no longer appear in future scans.\n"
            + "The templates will remain unchanged.",
            "Keep Both Templates");
        if (!confirmed) return;

        using var ctx = DatabaseInitializer.CreateContext();
        
        // Check if already ignored (shouldn't happen, but defensive coding).
        var existing = ctx.IgnoredTemplateDuplicates
            .FirstOrDefault(i => i.SharedLink == group.SharedLink && i.LinkType == group.LinkType);
        
        if (existing == null)
        {
            ctx.IgnoredTemplateDuplicates.Add(new IgnoredTemplateDuplicate
            {
                SharedLink = group.SharedLink,
                LinkType = group.LinkType,
                IgnoredAt = DateTime.UtcNow
            });
            ctx.SaveChanges();
        }

        _tplGroups.Remove(group);
        RebuildTemplatePanel();

        StatusLabel.Foreground            = AppColors.Success;
        StatusLabel.Text                  = $"Marked duplicate as ignored. {_tplGroups.Count} group(s) remaining.";
        MergeAllTemplatesButton.IsEnabled = _tplGroups.Count > 0;
    }
}
