using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>One selectable description row shown in the <see cref="DescriptionFilterDialog"/> list.</summary>
public sealed class DescriptionFilterRow : INotifyPropertyChanged
{
    private bool _isSelected;

    /// <summary>Gets the description text displayed next to the checkbox.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Gets or sets whether this description is included in the generated cover sheet.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Modal dialog listing every description used by a manufacturer's hardware items on a job,
/// each with a checkbox, plus a "Select All" checkbox at the top. Returns the set of selected
/// description labels, or <c>null</c> if the dialog was cancelled.
/// </summary>
public partial class DescriptionFilterDialog : Window
{
    private readonly List<DescriptionFilterRow> _rows;
    private bool _suppressSelectAllHandler;

    /// <summary>Required by the Avalonia XAML compiler.</summary>
    public DescriptionFilterDialog() : this(new List<string>(), null) { }

    /// <summary>Initializes the dialog with the distinct descriptions available for the manufacturer.</summary>
    /// <param name="descriptions">The distinct description labels to list.</param>
    /// <param name="currentlySelected">
    /// The previously-selected labels (if any), used to restore checkbox state. When <c>null</c>,
    /// every description starts checked.
    /// </param>
    public DescriptionFilterDialog(List<string> descriptions, HashSet<string>? currentlySelected)
    {
        _rows = descriptions
            .Select(d => new DescriptionFilterRow
            {
                Label = d,
                IsSelected = currentlySelected == null || currentlySelected.Contains(d)
            })
            .ToList();

        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        DescriptionsList.ItemsSource = _rows;

        foreach (var row in _rows)
            row.PropertyChanged += (_, _) => UpdateSelectAllState();

        UpdateSelectAllState();

        SelectAllCheckBox.Click += (_, _) =>
        {
            if (_suppressSelectAllHandler) return;
            var isChecked = SelectAllCheckBox.IsChecked == true;
            foreach (var row in _rows)
                row.IsSelected = isChecked;
        };

        OkButton.Click += (_, _) => Close(new HashSet<string>(_rows.Where(r => r.IsSelected).Select(r => r.Label)));
        CancelButton.Click += (_, _) => Close(null);
    }

    private void UpdateSelectAllState()
    {
        _suppressSelectAllHandler = true;
        try
        {
            SelectAllCheckBox.IsChecked = _rows.Count > 0 && _rows.All(r => r.IsSelected);
        }
        finally
        {
            _suppressSelectAllHandler = false;
        }
    }
}
