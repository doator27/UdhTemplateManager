using System;
using System.Collections.Generic;
using System.Linq;
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
/// Scrollable bulk-entry table where the user groups hardware items by manufacturer
/// before launching the template resolution wizard.
/// </summary>
public partial class BulkHardwareEntryView : UserControl
{
    private readonly int _jobId;
    private List<Manufacturer> _manufacturers = new();
    private List<Description> _allDescriptions = new();
    private List<DescriptionComboItem> _allDescComboItems = new();

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    // ── Group model ───────────────────────────────────────────────────────────

    /// <summary>One manufacturer section containing one or more hardware rows.</summary>
    private sealed class MfrGroup
    {
        /// <summary>Gets or sets the currently selected manufacturer for this group.</summary>
        public Manufacturer? Manufacturer { get; set; }

        /// <summary>In-memory rows belonging to this group.</summary>
        public List<BulkHardwareRow> Rows { get; } = new();

        /// <summary>The UI panel that holds the row grids for this group.</summary>
        public StackPanel RowsPanel { get; set; } = null!;

        /// <summary>The outer border element for the whole group.</summary>
        public Border GroupBorder { get; set; } = null!;

        /// <summary>Per-row callbacks that refresh model items and match state when the manufacturer changes.</summary>
        public Dictionary<BulkHardwareRow, Action> RefreshActions { get; } = new();
    }

    private readonly List<MfrGroup> _groups = new();

    /// <summary>Initializes the bulk entry view for the given job.</summary>
    /// <param name="jobId">The job to which hardware will be added.</param>
    public BulkHardwareEntryView(int jobId)
    {
        _jobId = jobId;
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        using var ctx = DatabaseInitializer.CreateContext();
        _manufacturers     = new ManufacturerRepository(ctx).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        _allDescriptions   = new DescriptionRepository(ctx).GetAll().ToList();
        _allDescComboItems = DescriptionHelper.BuildComboItems(_allDescriptions);

        BackButton.Click     += (_, _) => NavigationRequested?.Invoke($"JobDetail:{_jobId}");
        AddGroupButton.Click += (_, _) => AddGroup();
        ContinueButton.Click += (_, _) => OnContinue();

        AddGroup();
    }

    // ── Group building ────────────────────────────────────────────────────────

    /// <summary>Appends a new manufacturer group (with one blank row) to the panel.</summary>
    private void AddGroup()
    {
        var group = new MfrGroup();
        _groups.Add(group);

        // Manufacturer combo for this group
        var mfrCombo = new ComboBox
        {
            ItemsSource = _manufacturers,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 200,
            Margin = new Avalonia.Thickness(0, 0, 8, 0)
        };
        mfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");

        var addItemBtn = new Button
        {
            Content = "+ Add Item",
            FontSize = 11,
            Padding = new Avalonia.Thickness(6, 2)
        };

        var removeGroupBtn = new Button
        {
            Content = "Remove Group",
            FontSize = 10,
            Padding = new Avalonia.Thickness(4, 1),
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            Foreground = Brushes.DarkRed
        };

        var mfrHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Avalonia.Thickness(0, 0, 0, 4)
        };
        mfrHeader.Children.Add(new TextBlock
        {
            Text = "Manufacturer:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 4, 0)
        });
        mfrHeader.Children.Add(mfrCombo);
        mfrHeader.Children.Add(addItemBtn);
        mfrHeader.Children.Add(removeGroupBtn);

        var rowsPanel = new StackPanel();
        group.RowsPanel = rowsPanel;

        var groupContent = new StackPanel { Margin = new Avalonia.Thickness(4) };
        groupContent.Children.Add(mfrHeader);
        groupContent.Children.Add(rowsPanel);

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

        // When the manufacturer changes, propagate to all rows in this group.
        mfrCombo.SelectionChanged += (_, _) =>
        {
            group.Manufacturer = mfrCombo.SelectedItem as Manufacturer;
            foreach (var row in group.Rows)
            {
                row.SelectedManufacturer = group.Manufacturer;
                if (group.RefreshActions.TryGetValue(row, out var refresh))
                    refresh();
            }
            RefreshContinue();
        };

        addItemBtn.Click += (_, _) => AddRowToGroup(group);

        removeGroupBtn.Click += (_, _) =>
        {
            _groups.Remove(group);
            GroupsPanel.Children.Remove(groupBorder);
            RefreshContinue();
        };

        AddRowToGroup(group);
    }

    // ── Row building ──────────────────────────────────────────────────────────

    /// <summary>Appends a new blank hardware row to <paramref name="group"/>.</summary>
    private void AddRowToGroup(MfrGroup group)
    {
        var row = new BulkHardwareRow { SelectedManufacturer = group.Manufacturer };
        group.Rows.Add(row);

        // Column widths: Description, Model#, Match, Custom Label, Remarks, Remove
        // These must match the header grid in the AXAML.
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("260,210,90,170,210,36"),
            Margin = new Avalonia.Thickness(0, 0, 0, 8)
        };

        // ── Col 0: Description picker (search + tree + new) ──────────────────
        var descSearchBox = new TextBox
        {
            Watermark = "Search descriptions…",
            FontSize = 11,
            Margin = new Avalonia.Thickness(0, 0, 0, 2)
        };
        var descMatchList = new ListBox { MaxHeight = 80, IsVisible = false };
        descMatchList.DisplayMemberBinding = new Avalonia.Data.Binding("DisplayText");
        var descSelectedLabel = new TextBlock
        {
            Text = "(none)",
            Foreground = Brushes.Gray,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap
        };
        var descPickTreeBtn = new Button
        {
            Content = "⋯ Tree",
            FontSize = 10,
            Padding = new Avalonia.Thickness(4, 1)
        };
        var descNewBtn = new Button
        {
            Content = "+ New",
            FontSize = 10,
            Padding = new Avalonia.Thickness(4, 1)
        };
        var descBtnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Avalonia.Thickness(0, 2, 0, 0)
        };
        descBtnRow.Children.Add(descPickTreeBtn);
        descBtnRow.Children.Add(descNewBtn);
        var descStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            Margin = new Avalonia.Thickness(0, 0, 8, 0)
        };
        descStack.Children.Add(descSearchBox);
        descStack.Children.Add(descMatchList);
        descStack.Children.Add(descSelectedLabel);
        descStack.Children.Add(descBtnRow);
        Grid.SetColumn(descStack, 0);

        // ── Col 1: Model # — editable ComboBox ───────────────────────────────
        var modelCombo = new ComboBox
        {
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(0, 0, 8, 0)
        };
        modelCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ModelNumber");
        Grid.SetColumn(modelCombo, 1);

        // ── Col 2: Match status ───────────────────────────────────────────────
        var matchLabel = new TextBlock
        {
            Text = "—",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(4, 4, 8, 0),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(matchLabel, 2);

        // ── Col 3: Custom label ───────────────────────────────────────────────
        var customBox = new TextBox
        {
            Watermark = "Optional",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(0, 0, 8, 0),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(customBox, 3);

        // ── Col 4: Remarks ────────────────────────────────────────────────────
        var remarksBox = new TextBox
        {
            Watermark = "Optional",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(0, 0, 8, 0),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(remarksBox, 4);

        // ── Col 5: Remove button ──────────────────────────────────────────────
        var removeBtn = new Button
        {
            Content = "×",
            Width = 28,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(removeBtn, 5);

        grid.Children.Add(descStack);
        grid.Children.Add(modelCombo);
        grid.Children.Add(matchLabel);
        grid.Children.Add(customBox);
        grid.Children.Add(remarksBox);
        grid.Children.Add(removeBtn);
        group.RowsPanel.Children.Add(grid);

        // ── Local helpers ─────────────────────────────────────────────────────

        void RefreshModelItems()
        {
            if (row.SelectedManufacturer == null && row.SelectedDescription == null)
            {
                modelCombo.ItemsSource = null;
                return;
            }
            using var ctx = DatabaseInitializer.CreateContext();
            var q = ctx.HardwareItems.AsQueryable();
            if (row.SelectedManufacturer != null)
                q = q.Where(h => h.ManufacturerId == row.SelectedManufacturer.Id);
            if (row.SelectedDescription != null)
                q = q.Where(h => h.DescriptionId == row.SelectedDescription.Id);
            modelCombo.ItemsSource = q.OrderBy(h => h.ModelNumber).ToList();
        }

        void SyncModelState()
        {
            var text = modelCombo.Text?.Trim() ?? "";
            row.ModelNumber = text;

            if (string.IsNullOrWhiteSpace(text))
            {
                row.MatchedItem = null;
            }
            else if (modelCombo.SelectedItem is HardwareItem h && h.ModelNumber == text)
            {
                row.MatchedItem = h;
            }
            else
            {
                row.MatchedItem = null;
                if (row.SelectedManufacturer != null && row.SelectedDescription != null)
                {
                    using var ctx = DatabaseInitializer.CreateContext();
                    row.MatchedItem = ctx.HardwareItems.FirstOrDefault(item =>
                        item.ManufacturerId == row.SelectedManufacturer.Id &&
                        item.DescriptionId  == row.SelectedDescription.Id &&
                        item.ModelNumber    == text);
                }
            }

            UpdateMatchLabel(row, matchLabel);
            RefreshContinue();
        }

        // Register with group so manufacturer changes can trigger refresh.
        group.RefreshActions[row] = () => { RefreshModelItems(); SyncModelState(); };

        // ── Description picker events ─────────────────────────────────────────

        void SelectDescription(Description? desc)
        {
            row.SelectedDescription      = desc;
            descSelectedLabel.Text       = desc != null ? BuildDescPath(desc) : "(none)";
            descSelectedLabel.Foreground = desc != null ? Brushes.Black : Brushes.Gray;
            descSearchBox.Text           = "";
            descMatchList.IsVisible      = false;
            RefreshModelItems();
            SyncModelState();
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

        descPickTreeBtn.Click += async (_, _) =>
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
            var newDesc = new DescriptionRepository(ctx).Add(new Description { DescriptionText = name, ParentId = null });
            RefreshDescriptions();
            SelectDescription(_allDescriptions.FirstOrDefault(d => d.Id == newDesc.Id));
        };

        // ── Model combo events ────────────────────────────────────────────────

        modelCombo.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty)
                SyncModelState();
        };
        modelCombo.SelectionChanged += (_, _) => SyncModelState();

        // ── Other column events ───────────────────────────────────────────────

        customBox.TextChanged += (_, _) =>
        {
            row.CustomLabel = string.IsNullOrWhiteSpace(customBox.Text) ? null : customBox.Text.Trim();
        };

        remarksBox.TextChanged += (_, _) =>
        {
            row.Remarks = string.IsNullOrWhiteSpace(remarksBox.Text) ? null : remarksBox.Text.Trim();
        };

        removeBtn.Click += (_, _) =>
        {
            group.Rows.Remove(row);
            group.RefreshActions.Remove(row);
            group.RowsPanel.Children.Remove(grid);
            // Remove the entire group if it now has no rows.
            if (group.Rows.Count == 0)
            {
                _groups.Remove(group);
                GroupsPanel.Children.Remove(group.GroupBorder);
            }
            RefreshContinue();
        };
    }

    // ── State helpers ──────────────────────────────────────────────────────────

    private static void UpdateMatchLabel(BulkHardwareRow row, TextBlock label)
    {
        if (string.IsNullOrWhiteSpace(row.ModelNumber))
        {
            label.Text       = "—";
            label.Foreground = Brushes.Gray;
        }
        else if (row.MatchedItem != null)
        {
            label.Text       = "✓ Matched";
            label.Foreground = Brushes.DarkGreen;
        }
        else
        {
            label.Text       = "(new)";
            label.Foreground = Brushes.Gray;
        }
    }

    private void RefreshContinue()
    {
        ContinueButton.IsEnabled = _groups
            .SelectMany(g => g.Rows)
            .Any(r =>
                r.SelectedManufacturer != null &&
                r.SelectedDescription  != null &&
                !string.IsNullOrWhiteSpace(r.ModelNumber));
    }

    private void OnContinue()
    {
        var complete = _groups
            .SelectMany(g => g.Rows)
            .Where(r =>
                r.SelectedManufacturer != null &&
                r.SelectedDescription  != null &&
                !string.IsNullOrWhiteSpace(r.ModelNumber))
            .ToList();

        if (complete.Count == 0)
        {
            StatusLabel.Text = "Add at least one complete row.";
            return;
        }

        BulkAddSession.JobId       = _jobId;
        BulkAddSession.PendingRows = complete;
        NavigationRequested?.Invoke($"TemplateResolutionWizard:{_jobId}");
    }

    // ── Utilities ──────────────────────────────────────────────────────────────

    /// <summary>Reloads descriptions from the database and rebuilds the combo item list.</summary>
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
