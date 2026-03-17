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
        _manufacturers   = new ManufacturerRepository(ctx).GetAll().OrderBy(m => m.ManufacturerName).ToList();
        _allDescriptions = new DescriptionRepository(ctx).GetAll().ToList();

        BackButton.Click    += (_, _) => NavigationRequested?.Invoke($"JobDetail:{_jobId}");
        AddRowButton.Click  += (_, _) => AddRow();
        ContinueButton.Click += (_, _) => OnContinue();

        AddRow();
    }

    /// <summary>Appends a new blank data-entry row to the panel.</summary>
    private void AddRow()
    {
        var row = new BulkHardwareRow();
        _rows.Add(row);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("160,180,140,80,120,32"),
            Margin = new Avalonia.Thickness(0, 0, 0, 4)
        };

        // Manufacturer ComboBox
        var mfrCombo = new ComboBox
        {
            ItemsSource = _manufacturers,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(0, 0, 4, 0)
        };
        mfrCombo.DisplayMemberBinding = new Avalonia.Data.Binding("ManufacturerName");
        Grid.SetColumn(mfrCombo, 0);

        // Description Button + label in a stack
        var descLabel = new TextBlock
        {
            Text = "(none)",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.Gray,
            FontSize = 11,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
        };
        var descButton = new Button
        {
            Content = "Pick…",
            FontSize = 11,
            Padding = new Avalonia.Thickness(4, 1)
        };
        var descStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Avalonia.Thickness(0, 0, 4, 0) };
        descStack.Children.Add(descButton);
        descStack.Children.Add(descLabel);
        Grid.SetColumn(descStack, 1);

        // Model TextBox
        var modelBox = new TextBox
        {
            Watermark = "Model #",
            Margin = new Avalonia.Thickness(0, 0, 4, 0)
        };
        Grid.SetColumn(modelBox, 2);

        // Match status
        var matchLabel = new TextBlock
        {
            Text = "—",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(2, 0, 4, 0),
            FontSize = 11
        };
        Grid.SetColumn(matchLabel, 3);

        // Custom label TextBox
        var customBox = new TextBox
        {
            Watermark = "Optional",
            Margin = new Avalonia.Thickness(0, 0, 4, 0)
        };
        Grid.SetColumn(customBox, 4);

        // Remove button
        var removeBtn = new Button
        {
            Content = "×",
            Width = 28,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(removeBtn, 5);

        grid.Children.Add(mfrCombo);
        grid.Children.Add(descStack);
        grid.Children.Add(modelBox);
        grid.Children.Add(matchLabel);
        grid.Children.Add(customBox);
        grid.Children.Add(removeBtn);

        RowsPanel.Children.Add(grid);

        // Wire events
        mfrCombo.SelectionChanged += (_, _) =>
        {
            row.SelectedManufacturer = mfrCombo.SelectedItem as Manufacturer;
            CheckMatch(row, matchLabel);
            RefreshContinue();
        };

        descButton.Click += async (_, _) =>
        {
            var picked = await OpenDescriptionPickerAsync();
            if (picked.HasValue)
            {
                var desc = _allDescriptions.FirstOrDefault(d => d.Id == picked.Value);
                row.SelectedDescription = desc;
                descLabel.Text = desc != null ? BuildDescPath(desc) : "(none)";
                descLabel.Foreground = Brushes.Black;
            }
            CheckMatch(row, matchLabel);
            RefreshContinue();
        };

        modelBox.TextChanged += (_, _) =>
        {
            row.ModelNumber = modelBox.Text?.Trim() ?? "";
            CheckMatch(row, matchLabel);
            RefreshContinue();
        };

        customBox.TextChanged += (_, _) =>
        {
            row.CustomLabel = string.IsNullOrWhiteSpace(customBox.Text) ? null : customBox.Text.Trim();
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

    /// <summary>
    /// Checks whether the row's Manufacturer + Description + ModelNumber match an existing
    /// <see cref="HardwareItem"/> and updates the match status label accordingly.
    /// </summary>
    private void CheckMatch(BulkHardwareRow row, TextBlock matchLabel)
    {
        if (row.SelectedManufacturer == null || row.SelectedDescription == null || string.IsNullOrWhiteSpace(row.ModelNumber))
        {
            row.MatchedItem = null;
            matchLabel.Text = "—";
            matchLabel.Foreground = Brushes.Gray;
            return;
        }

        using var ctx = DatabaseInitializer.CreateContext();
        var match = ctx.HardwareItems.FirstOrDefault(h =>
            h.ManufacturerId == row.SelectedManufacturer.Id &&
            h.DescriptionId  == row.SelectedDescription.Id &&
            h.ModelNumber    == row.ModelNumber);

        row.MatchedItem = match;
        if (match != null)
        {
            matchLabel.Text = "✓ Matched";
            matchLabel.Foreground = Brushes.DarkGreen;
        }
        else
        {
            matchLabel.Text = "(new)";
            matchLabel.Foreground = Brushes.Gray;
        }
    }

    /// <summary>
    /// Enables the Continue button when at least one row has Manufacturer, Description,
    /// and ModelNumber all set.
    /// </summary>
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

    /// <summary>Opens the <see cref="DescriptionPickerWindow"/> and returns the selected ID.</summary>
    private async Task<int?> OpenDescriptionPickerAsync()
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window == null) return null;

        var picker = new DescriptionPickerWindow(_allDescriptions);
        return await picker.ShowDialog<int?>(window);
    }

    /// <summary>Builds the full ancestor path string for a description node.</summary>
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
