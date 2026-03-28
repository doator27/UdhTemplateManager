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

namespace HardwareTemplateBuilder.App.Views;

/// <summary>
/// Per-manufacturer bulk hardware entry screen.
/// The user adds hardware item groups (description + model# + template#) and then one or
/// more callout rows per group (custom label + per-callout remarks).
/// Navigates back to <see cref="BulkManufacturerSessionView"/> on Back, auto-saving the draft.
/// </summary>
public partial class BulkHardwareEntryView : UserControl
{
    private readonly int _jobId;
    private readonly int _manufacturerId;
    private Manufacturer? _manufacturer;

    private List<Description> _allDescriptions = new();
    private List<DescriptionComboItem> _allDescComboItems = new();

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    // ── Group model ───────────────────────────────────────────────────────────

    private sealed class HwItemGroup
    {
        public Description? SelectedDescription { get; set; }
        public string ModelNumber { get; set; } = "";
        public string? TemplateNumber { get; set; }
        public HardwareItem? MatchedItem { get; set; }
        public List<CalloutRow> Callouts { get; } = new();
        public StackPanel CalloutsPanel { get; set; } = null!;
        public Border GroupBorder { get; set; } = null!;
        public TextBlock MatchLabel { get; set; } = null!;

        // UI controls we need to write back to on restore
        public TextBox? ModelBox { get; set; }
        public TextBox? TemplateBox { get; set; }
        public TextBlock? DescLabel { get; set; }
    }

    private sealed class CalloutRow
    {
        public string? Label { get; set; }
        public string? CalloutRemarks { get; set; }
        // UI controls for restore
        public TextBox? LabelBox { get; set; }
        public TextBox? RemarksBox { get; set; }
    }

    private readonly List<HwItemGroup> _groups = new();

    /// <summary>
    /// Initializes the entry view scoped to a single manufacturer.
    /// </summary>
    /// <param name="jobId">The job being built.</param>
    /// <param name="manufacturerId">The manufacturer whose hardware is being entered.</param>
    public BulkHardwareEntryView(int jobId, int manufacturerId)
    {
        _jobId          = jobId;
        _manufacturerId = manufacturerId;
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _manufacturer        = ctx.Manufacturers.Find(_manufacturerId);
        _allDescriptions     = new DescriptionRepository(ctx).GetAll().ToList();
        _allDescComboItems   = DescriptionHelper.BuildComboItems(_allDescriptions);

        TitleLabel.Text = $"Bulk Entry — {_manufacturer?.ManufacturerName ?? "Unknown"}";

        BackButton.Click    += (_, _) => { SaveDraft(); NavigationRequested?.Invoke($"BulkManufacturerSession:{_jobId}"); };
        AddItemButton.Click += (_, _) => AddItemGroup();

        if (!RestoreDraft())
            AddItemGroup();
    }

    // ── Group building ────────────────────────────────────────────────────────

    /// <summary>Appends a new hardware-item group (with one blank callout) to the panel.</summary>
    private void AddItemGroup()
    {
        var group = new HwItemGroup();
        _groups.Add(group);

        // ── Top row: Description | Model # | Template # | Match | Remove ──────

        var descSearchBox = new TextBox
        {
            Watermark = "Search descriptions…",
            FontSize = 11,
            Margin = new Avalonia.Thickness(0, 0, 0, 2)
        };
        var descMatchList = new ListBox { MaxHeight = 80, IsVisible = false };
        descMatchList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        var descLabel = new TextBlock
        {
            Text = "(none)",
            Foreground = Brushes.Gray,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap
        };
        group.DescLabel = descLabel;
        var descPickBtn = new Button { Content = "⋯", FontSize = 10, Padding = new Avalonia.Thickness(4, 1) };
        var descNewBtn  = new Button { Content = "+", FontSize = 10, Padding = new Avalonia.Thickness(4, 1) };
        var descBtns    = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Avalonia.Thickness(0, 2, 0, 0) };
        descBtns.Children.Add(descPickBtn);
        descBtns.Children.Add(descNewBtn);
        var descStack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, Margin = new Avalonia.Thickness(0, 0, 8, 0) };
        descStack.Children.Add(descSearchBox);
        descStack.Children.Add(descMatchList);
        descStack.Children.Add(descLabel);
        descStack.Children.Add(descBtns);

        var modelCombo = new ComboBox
        {
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(0, 0, 8, 0)
        };
        modelCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ModelNumber");

        var tplBox = new TextBox
        {
            Watermark = "e.g. AL-123",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(0, 0, 8, 0)
        };
        group.TemplateBox = tplBox;

        var matchLabel = new TextBlock
        {
            Text = "—",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(4, 4, 8, 0),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        group.MatchLabel = matchLabel;

        var removeGroupBtn = new Button
        {
            Content = "✕ Remove",
            FontSize = 10,
            Padding = new Avalonia.Thickness(4, 1),
            Foreground = Brushes.DarkRed,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(0, 0, 0, 0)
        };

        var topGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("260,180,140,80,Auto"),
            Margin = new Avalonia.Thickness(0, 0, 0, 6)
        };
        Grid.SetColumn(descStack,      0);
        Grid.SetColumn(modelCombo,     1);
        Grid.SetColumn(tplBox,         2);
        Grid.SetColumn(matchLabel,     3);
        Grid.SetColumn(removeGroupBtn, 4);
        topGrid.Children.Add(descStack);
        topGrid.Children.Add(modelCombo);
        topGrid.Children.Add(tplBox);
        topGrid.Children.Add(matchLabel);
        topGrid.Children.Add(removeGroupBtn);

        // ── Callout sub-section ────────────────────────────────────────────────

        var calloutHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,28"),
            Margin = new Avalonia.Thickness(0, 4, 0, 2)
        };
        calloutHeader.Children.Add(new TextBlock { Text = "Custom Label", FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Brushes.DimGray, Margin = new Avalonia.Thickness(2, 0) });
        var calloutRemarksHeader = new TextBlock { Text = "Callout Remarks", FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Brushes.DimGray, Margin = new Avalonia.Thickness(2, 0) };
        Grid.SetColumn(calloutRemarksHeader, 1);
        calloutHeader.Children.Add(calloutRemarksHeader);

        var calloutsPanel = new StackPanel();
        group.CalloutsPanel = calloutsPanel;

        var addCalloutBtn = new Button
        {
            Content = "+ Add Callout",
            FontSize = 10,
            Padding = new Avalonia.Thickness(6, 2),
            Margin = new Avalonia.Thickness(0, 4, 0, 0)
        };
        addCalloutBtn.Click += (_, _) => AddCalloutRow(group);

        var groupContent = new StackPanel { Margin = new Avalonia.Thickness(4) };
        groupContent.Children.Add(topGrid);
        groupContent.Children.Add(calloutHeader);
        groupContent.Children.Add(calloutsPanel);
        groupContent.Children.Add(addCalloutBtn);

        var groupBorder = new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(2),
            Margin = new Avalonia.Thickness(0, 0, 0, 10),
            Padding = new Avalonia.Thickness(8, 6),
            Child = groupContent
        };
        group.GroupBorder = groupBorder;
        GroupsPanel.Children.Add(groupBorder);

        // Add first callout row automatically
        AddCalloutRow(group);

        // ── Events ────────────────────────────────────────────────────────────

        void RefreshModelItems()
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var q = ctx.HardwareItems.Where(h => h.ManufacturerId == _manufacturerId);
            if (group.SelectedDescription != null)
                q = q.Where(h => h.DescriptionId == group.SelectedDescription.Id);
            modelCombo.ItemsSource = q.OrderBy(h => h.ModelNumber).ToList();
        }

        void SyncMatchState()
        {
            var text = modelCombo.Text?.Trim() ?? "";
            group.ModelNumber = text;
            if (string.IsNullOrWhiteSpace(text))
            {
                group.MatchedItem = null;
            }
            else if (modelCombo.SelectedItem is HardwareItem h && h.ModelNumber == text)
            {
                group.MatchedItem = h;
            }
            else
            {
                using var ctx = DatabaseInitializer.CreateContext();
                group.MatchedItem = ctx.HardwareItems.FirstOrDefault(item =>
                    item.ManufacturerId == _manufacturerId &&
                    item.ModelNumber    == text &&
                    (group.SelectedDescription == null || item.DescriptionId == group.SelectedDescription.Id));
            }
            matchLabel.Text       = group.MatchedItem != null ? "✓ Matched" : (string.IsNullOrWhiteSpace(text) ? "—" : "(new)");
            matchLabel.Foreground = group.MatchedItem != null ? Brushes.DarkGreen : Brushes.Gray;
        }

        void SelectDescription(Description? desc)
        {
            group.SelectedDescription = desc;
            descLabel.Text       = desc != null ? BuildDescPath(desc) : "(none)";
            descLabel.Foreground = desc != null ? Brushes.Black : Brushes.Gray;
            descSearchBox.Text   = "";
            descMatchList.IsVisible = false;
            RefreshModelItems();
            SyncMatchState();
        }

        descSearchBox.TextChanged += (_, _) =>
        {
            var q = descSearchBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(q)) { descMatchList.IsVisible = false; return; }
            var matches = _allDescComboItems
                .Where(d => d.DisplayText.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Take(8).ToList();
            descMatchList.ItemsSource = matches;
            descMatchList.IsVisible   = matches.Count > 0;
        };
        descMatchList.SelectionChanged += (_, _) =>
        {
            if (descMatchList.SelectedItem is DescriptionComboItem item)
                SelectDescription(_allDescriptions.FirstOrDefault(d => d.Id == item.Id));
        };
        descPickBtn.Click += async (_, _) =>
        {
            var picked = await OpenDescriptionPickerAsync();
            if (picked.HasValue)
                SelectDescription(_allDescriptions.FirstOrDefault(d => d.Id == picked.Value));
        };
        descNewBtn.Click += (_, _) =>
        {
            var name = descSearchBox.Text?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            using var ctx = DatabaseInitializer.CreateContext();
            var newDesc = new DescriptionRepository(ctx).Add(new Description { DescriptionText = name });
            RefreshDescriptions();
            SelectDescription(_allDescriptions.FirstOrDefault(d => d.Id == newDesc.Id));
        };

        modelCombo.PropertyChanged += (_, e) => { if (e.Property == ComboBox.TextProperty) SyncMatchState(); };
        modelCombo.SelectionChanged += (_, _) => SyncMatchState();

        tplBox.TextChanged += (_, _) =>
            group.TemplateNumber = string.IsNullOrWhiteSpace(tplBox.Text) ? null : tplBox.Text.Trim();

        removeGroupBtn.Click += (_, _) =>
        {
            _groups.Remove(group);
            GroupsPanel.Children.Remove(groupBorder);
        };

        // Store model combo reference for restore
        group.ModelBox = new TextBox(); // placeholder — we'll write directly to modelCombo.Text
        // We need a reference to modelCombo for restore; store it via a closure capture
        // by using a small helper on the group object.
        _groupModelComboMap[group] = modelCombo;
    }

    // Maps each group to its model ComboBox so restore can set the text.
    private readonly Dictionary<HwItemGroup, ComboBox> _groupModelComboMap = new();

    // ── Callout row ───────────────────────────────────────────────────────────

    private void AddCalloutRow(HwItemGroup group, string? label = null, string? remarks = null)
    {
        var callout = new CalloutRow { Label = label, CalloutRemarks = remarks };
        group.Callouts.Add(callout);

        var labelBox = new TextBox
        {
            Text = label ?? "",
            Watermark = "Custom label (optional)",
            Margin = new Avalonia.Thickness(0, 0, 8, 0),
            TextWrapping = TextWrapping.Wrap
        };
        var remarksBox = new TextBox
        {
            Text = remarks ?? "",
            Watermark = "Callout remarks (optional)",
            Margin = new Avalonia.Thickness(0, 0, 8, 0),
            TextWrapping = TextWrapping.Wrap
        };
        var removeBtn = new Button
        {
            Content = "×",
            Width = 28,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,28"),
            Margin = new Avalonia.Thickness(0, 0, 0, 4)
        };
        Grid.SetColumn(labelBox,   0);
        Grid.SetColumn(remarksBox, 1);
        Grid.SetColumn(removeBtn,  2);
        grid.Children.Add(labelBox);
        grid.Children.Add(remarksBox);
        grid.Children.Add(removeBtn);
        group.CalloutsPanel.Children.Add(grid);

        callout.LabelBox   = labelBox;
        callout.RemarksBox = remarksBox;

        labelBox.TextChanged   += (_, _) => callout.Label          = string.IsNullOrWhiteSpace(labelBox.Text) ? null : labelBox.Text.Trim();
        remarksBox.TextChanged += (_, _) => callout.CalloutRemarks = string.IsNullOrWhiteSpace(remarksBox.Text) ? null : remarksBox.Text.Trim();

        removeBtn.Click += (_, _) =>
        {
            group.Callouts.Remove(callout);
            group.CalloutsPanel.Children.Remove(grid);
            // Always keep at least one callout row per group.
            if (group.Callouts.Count == 0)
                AddCalloutRow(group);
        };
    }

    // ── Draft persistence ─────────────────────────────────────────────────────

    private void SaveDraft()
    {
        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var draft = new BulkAddDraftRepository(ctx).GetByJob(_jobId);
            BulkSessionDraft session;
            try
            {
                session = (draft != null && !string.IsNullOrWhiteSpace(draft.DraftJson) && draft.DraftJson != "[]")
                    ? JsonSerializer.Deserialize<BulkSessionDraft>(draft.DraftJson) ?? new BulkSessionDraft()
                    : new BulkSessionDraft();
            }
            catch { session = new BulkSessionDraft(); }

            // Replace this manufacturer's section.
            session.Manufacturers.RemoveAll(m => m.ManufacturerId == _manufacturerId);
            session.Manufacturers.Add(new BulkSessionMfr
            {
                ManufacturerId   = _manufacturerId,
                ManufacturerName = _manufacturer?.ManufacturerName ?? "",
                Groups           = _groups
                    .Where(g => g.SelectedDescription != null || !string.IsNullOrWhiteSpace(g.ModelNumber))
                    .Select(g => new BulkSessionGroup
                    {
                        DescriptionId  = g.SelectedDescription?.Id,
                        ModelNumber    = g.ModelNumber,
                        TemplateNumber = g.TemplateNumber,
                        Callouts       = g.Callouts
                            .Select(c => new BulkSessionCallout
                            {
                                Label          = c.Label,
                                CalloutRemarks = c.CalloutRemarks
                            }).ToList()
                    }).ToList()
            });

            new BulkAddDraftRepository(ctx).Upsert(_jobId, JsonSerializer.Serialize(session));
        }
        catch { }
    }

    private bool RestoreDraft()
    {
        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var draft = new BulkAddDraftRepository(ctx).GetByJob(_jobId);
            if (draft == null || string.IsNullOrWhiteSpace(draft.DraftJson) || draft.DraftJson == "[]")
                return false;

            var session = JsonSerializer.Deserialize<BulkSessionDraft>(draft.DraftJson);
            var mfrSection = session?.Manufacturers.FirstOrDefault(m => m.ManufacturerId == _manufacturerId);
            if (mfrSection == null || mfrSection.Groups.Count == 0) return false;

            var descLookup = _allDescriptions.ToDictionary(d => d.Id);

            foreach (var sg in mfrSection.Groups)
            {
                AddItemGroup();
                var group = _groups.Last();

                if (sg.DescriptionId.HasValue && descLookup.TryGetValue(sg.DescriptionId.Value, out var desc))
                {
                    group.SelectedDescription = desc;
                    if (group.DescLabel != null)
                    {
                        group.DescLabel.Text       = BuildDescPath(desc);
                        group.DescLabel.Foreground = Brushes.Black;
                    }
                }

                if (!string.IsNullOrWhiteSpace(sg.ModelNumber))
                {
                    group.ModelNumber = sg.ModelNumber;
                    if (_groupModelComboMap.TryGetValue(group, out var modelCombo))
                        modelCombo.Text = sg.ModelNumber;
                }

                if (!string.IsNullOrWhiteSpace(sg.TemplateNumber))
                {
                    group.TemplateNumber = sg.TemplateNumber;
                    if (group.TemplateBox != null)
                        group.TemplateBox.Text = sg.TemplateNumber;
                }

                // Replace the default blank callout with restored ones.
                group.Callouts.Clear();
                group.CalloutsPanel.Children.Clear();

                if (sg.Callouts.Count > 0)
                {
                    foreach (var sc in sg.Callouts)
                        AddCalloutRow(group, sc.Label, sc.CalloutRemarks);
                }
                else
                {
                    AddCalloutRow(group);
                }
            }

            StatusLabel.Text = "Draft restored.";
            return true;
        }
        catch { return false; }
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private void RefreshDescriptions()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _allDescriptions   = new DescriptionRepository(ctx).GetAll().ToList();
        _allDescComboItems = DescriptionHelper.BuildComboItems(_allDescriptions);
    }

    private async Task<int?> OpenDescriptionPickerAsync()
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return null;
        var picker = new DescriptionPickerWindow(_allDescriptions);
        return await picker.ShowDialog<int?>(window);
    }

    private string BuildDescPath(Description target)
    {
        var lookup  = _allDescriptions.ToDictionary(d => d.Id);
        var parts   = new List<string>();
        var current = target;
        while (current != null)
        {
            parts.Insert(0, current.DescriptionText);
            current = current.ParentId.HasValue && lookup.TryGetValue(current.ParentId.Value, out var p) ? p : null;
        }
        return string.Join(" / ", parts);
    }
}
