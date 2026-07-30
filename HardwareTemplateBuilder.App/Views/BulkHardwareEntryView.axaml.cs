using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using HardwareTemplateBuilder.App.Helpers;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Repositories;
using HardwareTemplateBuilder.Core.Services.Pdf;
using Microsoft.EntityFrameworkCore;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Per-manufacturer bulk hardware entry view. Displays item cards for a single
/// manufacturer — no manufacturer picker is shown. Each item supports multiple
/// custom labels, each of which becomes a distinct <see cref="JobHardware"/> line item.
/// </summary>
public partial class BulkHardwareEntryView : UserControl
{
    private readonly int _jobId;
    private readonly int _manufacturerId;
    private Manufacturer? _manufacturer;
    private List<Description> _allDescriptions = new();
    private List<DescriptionComboItem> _descComboItems = new();

    /// <summary>In-memory item with its matched state and UI restore callback.</summary>
    private sealed class ItemEntry
    {
        public int? DescriptionId { get; set; }

        public Description? Description { get; set; }

        public string ModelNumber { get; set; } = string.Empty;

        /// <summary>Remarks for the <see cref="HardwareItem"/> itself (not per-label).</summary>
        public string? HardwareItemRemarks { get; set; }

        public HardwareItem? MatchedItem { get; set; }

        /// <summary>Label rows; always contains at least one entry.</summary>
        public List<LabelEntry> Labels { get; } = new();

        /// <summary>Callback to update all UI controls from model state after restore.</summary>
        public Action? RestoreUI { get; set; }

        /// <summary>The card border element in the scroll panel.</summary>
        public Border Card { get; set; } = null!;

        /// <summary>Single-line summary row shown when this card is collapsed.</summary>
        public Control? CompactPanel { get; set; }

        /// <summary>Full-detail panel shown when this card is expanded.</summary>
        public Control? ExpandedPanel { get; set; }

        /// <summary>Refreshes the compact summary text blocks from current entry state.</summary>
        public Action? UpdateCompact { get; set; }
    }

    private sealed class LabelEntry
    {
        public string CustomLabel { get; set; } = string.Empty;

        public string? Remarks { get; set; }

        /// <summary>Removes this label from the UI panel; set after the row is built.</summary>
        public Action? RemoveFromUI { get; set; }
    }

    private readonly List<ItemEntry> _items = new();

    /// <summary>The item card currently shown in expanded (edit) state.</summary>
    private ItemEntry? _activeEntry;

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Initializes the view for the given job and manufacturer.</summary>
    /// <param name="jobId">Job being edited.</param>
    /// <param name="manufacturerId">The single manufacturer whose items are shown.</param>
    public BulkHardwareEntryView(int jobId, int manufacturerId)
    {
        _jobId = jobId;
        _manufacturerId = manufacturerId;
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _manufacturer = new ManufacturerRepository(ctx).GetById(_manufacturerId);
        _allDescriptions = new DescriptionRepository(ctx).GetAll().ToList();
        _descComboItems = DescriptionHelper.BuildComboItems(_allDescriptions);

        HeaderLabel.Text = _manufacturer != null
            ? $"{_manufacturer.ManufacturerName} — Items"
            : "Items";

        BackButton.Click += (_, _) =>
        {
            SaveDraft();
            NavigationRequested?.Invoke($"BulkJobHub:{_jobId}");
        };
        SaveButton.Click += (_, _) =>
        {
            SaveDraft();
            StatusLabel.Text = "Saved.";
        };
        GenerateCoverSheetButton.Click += async (_, _) => await GenerateCoverSheetAsync();
        AddItemButton.Click += (_, _) => AddItem(null);

        if (!RestoreFromDraft())
        {
            AddItem(null);
        }
    }

    /// <summary>
    /// Expands <paramref name="entry"/>, collapsing whichever entry was previously active.
    /// </summary>
    private void SetActiveEntry(ItemEntry entry)
    {
        if (_activeEntry == entry) return;

        if (_activeEntry != null)
        {
            _activeEntry.UpdateCompact?.Invoke();
            if (_activeEntry.CompactPanel  != null) _activeEntry.CompactPanel.IsVisible  = true;
            if (_activeEntry.ExpandedPanel != null) _activeEntry.ExpandedPanel.IsVisible = false;
        }

        _activeEntry = entry;
        if (entry.CompactPanel  != null) entry.CompactPanel.IsVisible  = false;
        if (entry.ExpandedPanel != null) entry.ExpandedPanel.IsVisible = true;
    }

    /// <summary>Appends a new item card to the panel, optionally pre-populated from a draft item.</summary>
    private void AddItem(BulkDraftItem? draft)
    {
        var entry = new ItemEntry();
        _items.Add(entry);

        // Editable ComboBox seeded with descriptions already used by this manufacturer's items.
        // The user may also type any text not in the list — the full-tree fallback list below
        // then shows matching descriptions from the global hierarchy.
        var descCombo = new ComboBox
        {
            IsEditable = true,
            PlaceholderText = "Description…",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = GetDescriptionsForManufacturer(_manufacturerId)
        };
        descCombo.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");

        // Full-tree fallback: shown when typed text doesn't match any manufacturer description.
        var descMatchList = new ListBox { MaxHeight = 80, IsVisible = false };
        descMatchList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        var descLabel = new TextBlock
        {
            Text = "(none)",
            Foreground = AppColors.Muted,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap
        };
        var pickTreeBtn = new Button { Content = "⋯ Tree", FontSize = 10, Padding = new Avalonia.Thickness(4, 1) };
        var newDescBtn = new Button { Content = "+ New", FontSize = 10, Padding = new Avalonia.Thickness(4, 1) };
        var descBtnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Avalonia.Thickness(0, 2, 0, 0)
        };
        descBtnRow.Children.Add(pickTreeBtn);
        descBtnRow.Children.Add(newDescBtn);
        var descStack = new StackPanel { Spacing = 2 };
        descStack.Children.Add(descCombo);
        descStack.Children.Add(descMatchList);
        descStack.Children.Add(descLabel);
        descStack.Children.Add(descBtnRow);

        var modelCombo = new ComboBox
        {
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        modelCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ModelNumber");

        var showAllModelsCheckBox = new CheckBox
        {
            Content = "Show all models",
            FontSize = 10,
            IsChecked = true,
            Margin = new Avalonia.Thickness(0, 2, 0, 0)
        };

        var modelStack = new StackPanel { Spacing = 2 };
        modelStack.Children.Add(modelCombo);
        modelStack.Children.Add(showAllModelsCheckBox);

        var itemRemarksBox = new TextBox
        {
            Watermark = "Item remarks (optional)",
            VerticalAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 60
        };

        var matchLabel = new TextBlock
        {
            Text = "",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = AppColors.Muted,
            TextWrapping = TextWrapping.Wrap
        };

        var removeItemBtn = new Button
        {
            Content = "× Remove",
            FontSize = 10,
            Foreground = AppColors.Danger,
            Padding = new Avalonia.Thickness(6, 2)
        };

        var topRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("250,160,*,70,Auto"),
            Margin = new Avalonia.Thickness(0, 0, 0, 8)
        };
        topRow.Children.Add(descStack);
        Grid.SetColumn(modelStack, 1);
        topRow.Children.Add(modelStack);
        Grid.SetColumn(itemRemarksBox, 2);
        topRow.Children.Add(itemRemarksBox);
        Grid.SetColumn(matchLabel, 3);
        topRow.Children.Add(matchLabel);
        Grid.SetColumn(removeItemBtn, 4);
        topRow.Children.Add(removeItemBtn);

        var labelsPanel = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        var labelsHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        labelsHeader.Children.Add(new TextBlock
        {
            Text = "Labels:",
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });
        var addLabelBtn = new Button
        {
            Content = "+ Add Label",
            FontSize = 10,
            Padding = new Avalonia.Thickness(6, 2)
        };
        labelsHeader.Children.Add(addLabelBtn);
        labelsPanel.Children.Add(labelsHeader);

        // ── Compact summary row (shown when this card is not the active/focused one) ──
        var compactDescText = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var compactModelText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var compactRemarksText = new TextBlock
        {
            FontSize = 11,
            Foreground = AppColors.Muted,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var compactMatchText = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };

        var compactRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("200,150,*,70"),
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent
        };
        Grid.SetColumn(compactDescText,   0); compactRow.Children.Add(compactDescText);
        Grid.SetColumn(compactModelText,  1); compactRow.Children.Add(compactModelText);
        Grid.SetColumn(compactRemarksText, 2); compactRow.Children.Add(compactRemarksText);
        Grid.SetColumn(compactMatchText,  3); compactRow.Children.Add(compactMatchText);

        entry.UpdateCompact = () =>
        {
            compactDescText.Text = entry.Description != null
                ? BuildDescPath(entry.Description)
                : "(no description)";
            compactDescText.Foreground = entry.Description != null ? AppColors.Primary : AppColors.Muted;

            compactModelText.Text = string.IsNullOrWhiteSpace(entry.ModelNumber)
                ? "(no model)" : entry.ModelNumber;

            compactRemarksText.Text = entry.HardwareItemRemarks ?? string.Empty;

            if (string.IsNullOrWhiteSpace(entry.ModelNumber))
            {
                compactMatchText.Text = "";
                compactMatchText.Foreground = AppColors.Muted;
            }
            else if (entry.MatchedItem != null)
            {
                compactMatchText.Text = " ✓ Matched";
                compactMatchText.Foreground = AppColors.Success;
            }
            else
            {
                compactMatchText.Text = " (new)";
                compactMatchText.Foreground = AppColors.Info;
            }
        };

        compactRow.PointerPressed += (_, _) => SetActiveEntry(entry);

        // ── Expanded content (full editor) ──────────────────────────────────────
        var expandedPanel = new StackPanel();
        expandedPanel.Children.Add(topRow);
        expandedPanel.Children.Add(labelsPanel);

        entry.CompactPanel  = compactRow;
        entry.ExpandedPanel = expandedPanel;

        var cardContent = new StackPanel();
        cardContent.Children.Add(compactRow);
        cardContent.Children.Add(expandedPanel);

        var card = new Border
        {
            BorderBrush = AppColors.Muted,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(2),
            Padding = new Avalonia.Thickness(10, 8),
            Child = cardContent
        };
        entry.Card = card;
        ItemsPanel.Children.Add(card);

        // New item is immediately the active (expanded) entry; any previously
        // active entry collapses to its summary row.
        SetActiveEntry(entry);

        // Initialize the model combo with all items for this manufacturer
        RefreshModelItems();

        void RefreshModelItems()
        {
            if (_manufacturer == null)
            {
                modelCombo.ItemsSource = null;
                return;
            }

            using var ctx = DatabaseInitializer.CreateContext();
            var q = ctx.HardwareItems.AsQueryable()
                .Where(h => h.ManufacturerId == _manufacturerId && h.IsActive);

            // Only filter by description if checkbox is unchecked and a description is selected
            if (showAllModelsCheckBox.IsChecked != true && entry.Description != null)
            {
                q = q.Where(h => h.DescriptionId == entry.Description.Id);
            }

            modelCombo.ItemsSource = q.OrderBy(h => h.ModelNumber).ToList();
        }

        void SyncMatch()
        {
            var text = modelCombo.Text?.Trim() ?? string.Empty;
            entry.ModelNumber = text;
            if (string.IsNullOrWhiteSpace(text))
            {
                entry.MatchedItem = null;
            }
            else if (modelCombo.SelectedItem is HardwareItem h && h.ModelNumber == text)
            {
                entry.MatchedItem = h;
                
                // If a full item was selected and we don't have a description yet (or "show all" is checked),
                // auto-populate the description from the matched item
                if (entry.Description == null || showAllModelsCheckBox.IsChecked == true)
                {
                    var matchedDesc = _allDescriptions.FirstOrDefault(d => d.Id == h.DescriptionId);
                    if (matchedDesc != null)
                    {
                        SelectDescription(matchedDesc);
                        return; // SelectDescription calls SyncMatch, avoid double processing
                    }
                }
            }
            else
            {
                entry.MatchedItem = null;
                if (entry.Description != null)
                {
                    using var ctx = DatabaseInitializer.CreateContext();
                    entry.MatchedItem = ctx.HardwareItems.FirstOrDefault(i =>
                        i.ManufacturerId == _manufacturerId &&
                        i.DescriptionId == entry.Description.Id &&
                        i.ModelNumber == text &&
                        i.IsActive);
                }
            }

            // Populate the remarks box from the matched item. If no match, leave
            // the current text so the user's manually typed remarks are preserved.
            if (entry.MatchedItem != null)
            {
                itemRemarksBox.Text = entry.MatchedItem.Remarks ?? string.Empty;
            }

            UpdateMatchLabel(matchLabel, entry.MatchedItem, text);
            entry.UpdateCompact?.Invoke();
        }

        // Guard to prevent re-entrant calls when SelectDescription programmatically
        // clears descCombo.Text and SelectedItem after a selection is made.
        bool suppressDescSync = false;

        void SelectDescription(Description? desc)
        {
            if (suppressDescSync) return;
            suppressDescSync = true;
            entry.Description = desc;
            entry.DescriptionId = desc?.Id;
            descLabel.Text = desc != null ? BuildDescPath(desc) : "(none)";
            descLabel.Foreground = desc != null ? AppColors.Primary : AppColors.Muted;
            descCombo.Text = string.Empty;
            descCombo.SelectedItem = null;
            descMatchList.IsVisible = false;
            
            // When a description is selected, uncheck "Show all models" to filter the list
            if (desc != null)
            {
                showAllModelsCheckBox.IsChecked = false;
            }
            
            suppressDescSync = false;
            RefreshModelItems();
            SyncMatch();
        }

        // When the user picks a manufacturer description from the dropdown.
        descCombo.SelectionChanged += (_, _) =>
        {
            if (!suppressDescSync && descCombo.SelectedItem is DescriptionComboItem item)
                SelectDescription(_allDescriptions.FirstOrDefault(d => d.Id == item.Id));
        };

        // When the user types text: show full-tree matches as a fallback list.
        descCombo.PropertyChanged += (_, e) =>
        {
            if (e.Property != ComboBox.TextProperty || suppressDescSync) return;
            var q = descCombo.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(q))
            {
                descMatchList.IsVisible = false;
                return;
            }
            var hits = _descComboItems
                .Where(d => d.DisplayText.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .ToList();
            descMatchList.ItemsSource = hits;
            descMatchList.IsVisible = hits.Count > 0;
        };

        descMatchList.SelectionChanged += (_, _) =>
        {
            if (descMatchList.SelectedItem is DescriptionComboItem item)
            {
                SelectDescription(_allDescriptions.FirstOrDefault(d => d.Id == item.Id));
            }
        };
        pickTreeBtn.Click += async (_, _) =>
        {
            var picked = await OpenDescriptionPickerAsync();
            if (picked.HasValue)
            {
                SelectDescription(_allDescriptions.FirstOrDefault(d => d.Id == picked.Value));
            }
        };
        newDescBtn.Click += (_, _) =>
        {
            var name = descCombo.Text?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            using var ctx = DatabaseInitializer.CreateContext();
            var newDesc = new DescriptionRepository(ctx).Add(new Description { DescriptionText = name });
            RefreshDescriptions();
            SelectDescription(_allDescriptions.FirstOrDefault(d => d.Id == newDesc.Id));
        };

        modelCombo.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty)
            {
                SyncMatch();
            }
        };
        modelCombo.SelectionChanged += (_, _) => SyncMatch();

        showAllModelsCheckBox.Click += (_, _) => RefreshModelItems();

        itemRemarksBox.TextChanged += (_, _) =>
        {
            entry.HardwareItemRemarks = string.IsNullOrWhiteSpace(itemRemarksBox.Text)
                ? null : itemRemarksBox.Text.Trim();
            entry.UpdateCompact?.Invoke();
        };

        addLabelBtn.Click += (_, _) => AddLabelRow(entry, labelsPanel, null);

        removeItemBtn.Click += (_, _) =>
        {
            if (_activeEntry == entry) _activeEntry = null;
            _items.Remove(entry);
            ItemsPanel.Children.Remove(card);
        };

        entry.RestoreUI = () =>
        {
            // Capture before SyncMatch (triggered by setting modelCombo.Text or
            // SelectDescription) overwrites entry.HardwareItemRemarks with the DB value.
            var savedRemarks = entry.HardwareItemRemarks;
            modelCombo.Text = entry.ModelNumber;
            SelectDescription(entry.Description);
            // Restore draft remarks AFTER SyncMatch has run so the draft value wins.
            itemRemarksBox.Text = savedRemarks ?? string.Empty;
        };

        if (draft != null)
        {
            if (draft.DescriptionId.HasValue)
            {
                entry.Description = _allDescriptions.FirstOrDefault(d => d.Id == draft.DescriptionId.Value);
            }

            entry.DescriptionId       = draft.DescriptionId;
            entry.ModelNumber         = draft.ModelNumber;
            entry.HardwareItemRemarks = draft.HardwareItemRemarks;

            Avalonia.Threading.Dispatcher.UIThread.Post(() => entry.RestoreUI?.Invoke());

            foreach (var lbl in draft.Labels)
            {
                AddLabelRow(entry, labelsPanel, lbl);
            }

            if (draft.Labels.Count == 0)
            {
                AddLabelRow(entry, labelsPanel, null);
            }
        }
        else
        {
            AddLabelRow(entry, labelsPanel, null);
        }
    }

    private void AddLabelRow(ItemEntry item, StackPanel labelsPanel, BulkDraftLabel? draft)
    {
        var label = new LabelEntry
        {
            CustomLabel = draft?.CustomLabel ?? string.Empty,
            Remarks = draft?.Remarks
        };
        item.Labels.Add(label);

        var customBox = new TextBox
        {
            Watermark = "Custom label (optional)",
            MinWidth = 160,
            Text = label.CustomLabel
        };
        var remarksBox = new TextBox
        {
            Watermark = "Remarks (optional)",
            MinWidth = 140,
            Text = label.Remarks ?? string.Empty
        };
        var removeBtn = new Button
        {
            Content = "×",
            Width = 24,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontSize = 10,
            IsVisible = item.Labels.Count > 1
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Avalonia.Thickness(0, 2, 0, 0)
        };
        row.Children.Add(customBox);
        row.Children.Add(remarksBox);
        row.Children.Add(removeBtn);
        labelsPanel.Children.Add(row);

        label.RemoveFromUI = () =>
        {
            item.Labels.Remove(label);
            labelsPanel.Children.Remove(row);
            UpdateLabelRemoveButtons(item, labelsPanel);
        };

        customBox.TextChanged += (_, _) => label.CustomLabel = customBox.Text?.Trim() ?? string.Empty;
        remarksBox.TextChanged += (_, _) =>
            label.Remarks = string.IsNullOrWhiteSpace(remarksBox.Text) ? null : remarksBox.Text.Trim();
        removeBtn.Click += (_, _) => label.RemoveFromUI?.Invoke();

        UpdateLabelRemoveButtons(item, labelsPanel);
    }

    private static void UpdateLabelRemoveButtons(ItemEntry item, StackPanel labelsPanel)
    {
        for (int i = 1; i < labelsPanel.Children.Count; i++)
        {
            if (labelsPanel.Children[i] is StackPanel rowPanel && rowPanel.Children.LastOrDefault() is Button btn)
            {
                btn.IsVisible = item.Labels.Count > 1;
            }
        }
    }

    private void SaveDraft()
    {
        var draft = BulkDraftService.Load(_jobId);
        draft.Items.RemoveAll(i => i.ManufacturerId == _manufacturerId);

        foreach (var item in _items.Where(i => !string.IsNullOrWhiteSpace(i.ModelNumber)))
        {
            draft.Items.Add(new BulkDraftItem
            {
                ManufacturerId      = _manufacturerId,
                DescriptionId       = item.DescriptionId,
                ModelNumber         = item.ModelNumber,
                HardwareItemRemarks = item.HardwareItemRemarks,
                Labels = item.Labels.Select(l => new BulkDraftLabel
                {
                    CustomLabel = l.CustomLabel,
                    Remarks = l.Remarks
                }).ToList()
            });
        }

        BulkDraftService.Save(_jobId, draft);
    }

    private bool RestoreFromDraft()
    {
        var draft = BulkDraftService.Load(_jobId);
        var myItems = draft.Items.Where(i => i.ManufacturerId == _manufacturerId).ToList();
        if (myItems.Count == 0)
        {
            return false;
        }

        foreach (var item in myItems)
        {
            AddItem(item);
        }

        StatusLabel.Text = "Draft restored.";
        return true;
    }

    private static void UpdateMatchLabel(TextBlock label, HardwareItem? matched, string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            label.Text = "";
            label.Foreground = AppColors.Muted;
        }
        else if (matched != null)
        {
            label.Text = " ✓ Matched";
            label.Foreground = AppColors.Success;
        }
        else
        {
            label.Text = " (new)";
            label.Foreground = AppColors.Info;
        }
    }

    private void RefreshDescriptions()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _allDescriptions = new DescriptionRepository(ctx).GetAll().ToList();
        _descComboItems = DescriptionHelper.BuildComboItems(_allDescriptions);
    }

    private async Task<int?> OpenDescriptionPickerAsync()
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null)
        {
            return null;
        }

        var picker = new DescriptionPickerWindow(_allDescriptions);
        return await picker.ShowDialog<int?>(window);
    }

    /// <summary>
    /// Returns the subset of <see cref="_descComboItems"/> whose descriptions are already used
    /// by hardware items belonging to <paramref name="manufacturerId"/>. These are offered as
    /// quick-select options in the per-item description ComboBox.
    /// </summary>
    private List<DescriptionComboItem> GetDescriptionsForManufacturer(int manufacturerId)
    {
        using var ctx = DatabaseInitializer.CreateContext();
        var usedDescIds = ctx.HardwareItems
            .Where(h => h.ManufacturerId == manufacturerId && h.IsActive)
            .Select(h => h.DescriptionId)
            .Distinct()
            .ToHashSet();
        return _descComboItems.Where(d => usedDescIds.Contains(d.Id)).ToList();
    }

    private string BuildDescPath(Description target)
    {
        var lookup = _allDescriptions.ToDictionary(d => d.Id);
        var parts = new List<string>();
        var current = target;
        while (current != null)
        {
            parts.Insert(0, current.DescriptionText);
            current = current.ParentId.HasValue && lookup.TryGetValue(current.ParentId.Value, out var parent)
                ? parent
                : null;
        }

        return string.Join(" / ", parts);
    }

    /// <summary>
    /// Generates a cover sheet PDF showing all hardware items currently entered for this manufacturer.
    /// </summary>
    private async Task GenerateCoverSheetAsync()
    {
        if (_items.Count == 0 || _items.All(i => string.IsNullOrWhiteSpace(i.ModelNumber)))
        {
            StatusLabel.Text = "Add at least one item with a model number before generating.";
            StatusLabel.Foreground = AppColors.Danger;
            return;
        }

        StatusLabel.Text = "Generating cover sheet...";
        StatusLabel.Foreground = AppColors.Primary;

        string outputPath;
        try
        {
            outputPath = await Task.Run(() =>
            {
                using var ctx = DatabaseInitializer.CreateContext();
                var job = ctx.Jobs
                    .Include(j => j.Customer)
                    .Include(j => j.ProjectManager)
                    .FirstOrDefault(j => j.Id == _jobId);

                if (job == null)
                    throw new InvalidOperationException("Job not found.");

                var profile = ctx.UserProfiles.FirstOrDefault(u => u.Id == job.UserProfileId);
                var saveDir = new AppSettingRepository(ctx).GetValue("TemplateStorageLocation");
                if (string.IsNullOrWhiteSpace(saveDir))
                    saveDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                var rows = new List<CoverSheetRow>();

                // Build rows from the current in-memory items.
                foreach (var item in _items.Where(i => !string.IsNullOrWhiteSpace(i.ModelNumber)))
                {
                    var descText = item.Description?.DescriptionText ?? string.Empty;

                    // For each label in the item (or one unlabeled row if no labels).
                    var labelEntries = item.Labels.Count > 0
                        ? item.Labels
                        : new List<LabelEntry> { new LabelEntry() };

                    foreach (var label in labelEntries)
                    {
                        var hardwareDesc = !string.IsNullOrWhiteSpace(label.CustomLabel)
                            ? label.CustomLabel
                            : item.ModelNumber;

                        // Combine item-level and label-level remarks.
                        string? combinedRemarks = (item.HardwareItemRemarks, label.Remarks) switch
                        {
                            (null or "", null or "") => null,
                            (var a, null or "")      => a,
                            (null or "", var b)      => b,
                            (var a, var b)           => $"{a} | {b}"
                        };

                        rows.Add(new CoverSheetRow
                        {
                            Manufacturer        = _manufacturer?.ManufacturerName ?? "Unknown",
                            HardwareType        = descText,
                            HardwareDescription = hardwareDesc,
                            TemplateNumbers     = string.Empty,  // No template info in bulk entry
                            PageNumbers         = string.Empty,
                            Remarks             = combinedRemarks
                        });
                    }
                }

                if (rows.Count == 0)
                    throw new InvalidOperationException("No items to include in the cover sheet.");

                var coverData = new CoverSheetData
                {
                    JobNumber          = job.JobNumber,
                    JobName            = job.JobName,
                    CustomerName       = job.Customer?.CustomerName             ?? string.Empty,
                    ProjectManagerName = job.ProjectManager?.ProjectManagerName ?? string.Empty,
                    DateCreated        = DateTime.Now,
                    PreparedBy         = profile?.UserName ?? string.Empty,
                    Rows               = rows.AsReadOnly()
                };

                var safeName = string.Concat((_manufacturer?.ManufacturerName ?? "Unknown")
                    .Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
                var jobDir = Path.Combine(saveDir, job.JobNumber);
                Directory.CreateDirectory(jobDir);
                var outPath = Path.Combine(jobDir, $"Bulk Entry - {safeName}.pdf");

                new CoverSheetBuilder().Build(coverData, outPath);
                return outPath;
            });
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Cover sheet generation failed: {ex.Message}";
            StatusLabel.Foreground = AppColors.Danger;
            var win = TopLevel.GetTopLevel(this) as Window;
            if (win != null)
                await DialogHelper.ShowScrollableInfoAsync(win, ex.Message, "Cover Sheet Generation Failed");
            return;
        }

        StatusLabel.Text = $"Generated: {Path.GetFileName(outputPath)}";
        StatusLabel.Foreground = AppColors.Success;

        try
        {
            Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
        }
        catch
        {
            // Non-fatal: file was generated but couldn't be auto-opened.
        }
    }
}
