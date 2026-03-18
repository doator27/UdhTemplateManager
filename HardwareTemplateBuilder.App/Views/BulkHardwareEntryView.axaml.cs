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
/// Scrollable bulk-entry table where the user enters one hardware item per row before
/// launching the template resolution wizard.
/// </summary>
public partial class BulkHardwareEntryView : UserControl
{
    private readonly int _jobId;
    private List<Manufacturer> _manufacturers = new();
    private List<Description> _allDescriptions = new();
    private List<DescriptionComboItem> _allDescComboItems = new();

    /// <summary>Raised when the user requests navigation to a named view.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>In-memory rows parallel to the UI row controls.</summary>
    private readonly List<BulkHardwareRow> _rows = new();

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
        _manufacturers       = new ManufacturerRepository(ctx).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        _allDescriptions     = new DescriptionRepository(ctx).GetAll().ToList();
        _allDescComboItems   = DescriptionHelper.BuildComboItems(_allDescriptions);

        BackButton.Click     += (_, _) => NavigationRequested?.Invoke($"JobDetail:{_jobId}");
        AddRowButton.Click   += (_, _) => AddRow();
        ContinueButton.Click += (_, _) => OnContinue();

        AddRow();
    }

    // ── Row building ─────────────────────────────────────────────────────────

    /// <summary>Appends a new blank data-entry row to the panel.</summary>
    private void AddRow()
    {
        var row = new BulkHardwareRow();
        _rows.Add(row);

        // Column widths must match the header grid in the AXAML.
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("150,180,170,70,120,160,32"),
            Margin = new Avalonia.Thickness(0, 0, 0, 4)
        };

        // ── Col 0: Manufacturer ComboBox ──────────────────────────────────────
        var mfrCombo = new ComboBox
        {
            ItemsSource = _manufacturers,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(0, 0, 4, 0)
        };
        mfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        Grid.SetColumn(mfrCombo, 0);

        // ── Col 1: Description picker (search + tree + new) ──────────────────
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
            Spacing = 1,
            Margin = new Avalonia.Thickness(0, 0, 4, 0)
        };
        descStack.Children.Add(descSearchBox);
        descStack.Children.Add(descMatchList);
        descStack.Children.Add(descSelectedLabel);
        descStack.Children.Add(descBtnRow);
        Grid.SetColumn(descStack, 1);

        // ── Col 2: Model # — editable ComboBox ───────────────────────────────
        // Items are loaded from the DB when Manufacturer/Description change.
        // IsEditable=true lets the user type a new model number not in the list.
        var modelCombo = new ComboBox
        {
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(0, 0, 4, 0)
        };
        modelCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ModelNumber");
        Grid.SetColumn(modelCombo, 2);

        // ── Col 3: Match status ───────────────────────────────────────────────
        var matchLabel = new TextBlock
        {
            Text = "—",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(2, 0, 4, 0),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(matchLabel, 3);

        // ── Col 4: Custom label ───────────────────────────────────────────────
        var customBox = new TextBox
        {
            Watermark = "Optional",
            Margin = new Avalonia.Thickness(0, 0, 4, 0),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(customBox, 4);

        // ── Col 5: Remarks ────────────────────────────────────────────────────
        var remarksBox = new TextBox
        {
            Watermark = "Optional",
            Margin = new Avalonia.Thickness(0, 0, 4, 0),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(remarksBox, 5);

        // ── Col 6: Remove button ──────────────────────────────────────────────
        var removeBtn = new Button
        {
            Content = "×",
            Width = 28,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(removeBtn, 6);

        grid.Children.Add(mfrCombo);
        grid.Children.Add(descStack);
        grid.Children.Add(modelCombo);
        grid.Children.Add(matchLabel);
        grid.Children.Add(customBox);
        grid.Children.Add(remarksBox);
        grid.Children.Add(removeBtn);
        RowsPanel.Children.Add(grid);

        // ── Local helpers ────────────────────────────────────────────────────

        // Reloads the model combo's dropdown to show HardwareItems that match
        // the current manufacturer + description selection.
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

        // Syncs row.MatchedItem and row.ModelNumber from the combo's current state,
        // then updates the match label and the Continue button.
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
                // User picked an existing item from the dropdown.
                row.MatchedItem = h;
            }
            else
            {
                // User typed a value — check the DB for an exact match.
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

        // ── Wire events ──────────────────────────────────────────────────────

        mfrCombo.SelectionChanged += (_, _) =>
        {
            row.SelectedManufacturer = mfrCombo.SelectedItem as Manufacturer;
            RefreshModelItems();
            SyncModelState();
        };

        // Selects a description and updates all related state.
        void SelectDescription(Description? desc)
        {
            row.SelectedDescription         = desc;
            descSelectedLabel.Text          = desc != null ? BuildDescPath(desc) : "(none)";
            descSelectedLabel.Foreground    = desc != null ? Brushes.Black : Brushes.Gray;
            descSearchBox.Text              = "";
            descMatchList.IsVisible         = false;
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

        // Track text changes in the editable combo via property change notification.
        modelCombo.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty)
                SyncModelState();
        };
        // Also fire on explicit selection from the dropdown.
        modelCombo.SelectionChanged += (_, _) => SyncModelState();

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
            var idx = _rows.IndexOf(row);
            if (idx >= 0)
            {
                _rows.RemoveAt(idx);
                RowsPanel.Children.Remove(grid);
                RefreshContinue();
            }
        };
    }

    // ── State helpers ─────────────────────────────────────────────────────────

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
        ContinueButton.IsEnabled = _rows.Any(r =>
            r.SelectedManufacturer != null &&
            r.SelectedDescription  != null &&
            !string.IsNullOrWhiteSpace(r.ModelNumber));
    }

    private void OnContinue()
    {
        var complete = _rows.Where(r =>
            r.SelectedManufacturer != null &&
            r.SelectedDescription  != null &&
            !string.IsNullOrWhiteSpace(r.ModelNumber)).ToList();

        if (complete.Count == 0)
        {
            StatusLabel.Text = "Add at least one complete row.";
            return;
        }

        BulkAddSession.JobId       = _jobId;
        BulkAddSession.PendingRows = complete;
        NavigationRequested?.Invoke($"TemplateResolutionWizard:{_jobId}");
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

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
        var lookup = _allDescriptions.ToDictionary(d => d.Id);
        var parts  = new List<string>();
        var current = target;
        while (current != null)
        {
            parts.Insert(0, current.DescriptionText);
            current = current.ParentId.HasValue && lookup.TryGetValue(current.ParentId.Value, out var p) ? p : null;
        }
        return string.Join(" / ", parts);
    }
}
