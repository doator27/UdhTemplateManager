using System.Collections.Generic;
using Avalonia.Controls;

namespace HardwareTemplateBuilder.App.Views;

/// <summary>One hardware item that was skipped during the last "Add All to Job" run.</summary>
public sealed class FixItemRow
{
    /// <summary>Gets the manufacturer this item belongs to, for jumping back to its entry screen.</summary>
    public int ManufacturerId { get; init; }

    /// <summary>Gets the manufacturer's display name.</summary>
    public string ManufacturerName { get; init; } = string.Empty;

    /// <summary>Gets the model number typed for this item (or a placeholder if left blank).</summary>
    public string ModelNumber { get; init; } = string.Empty;

    /// <summary>Gets the reason this item was skipped.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Gets the single-line text shown for this row in the list.</summary>
    public string Display => $"{ManufacturerName} / {ModelNumber}: {Reason}";
}

/// <summary>
/// Modal dialog listing hardware items skipped during the last bulk "Add All to Job" run
/// (missing description, or an unexpected error). Returns the manufacturer ID to jump to for
/// fixing the selected item, or <c>null</c> if closed without a selection.
/// </summary>
public partial class ItemsToFixDialog : Window
{
    private readonly List<FixItemRow> _rows;

    /// <summary>Required by the Avalonia XAML compiler.</summary>
    public ItemsToFixDialog() : this(new List<FixItemRow>()) { }

    /// <summary>Initializes the dialog with the items that need fixing.</summary>
    /// <param name="rows">The skipped items to display.</param>
    public ItemsToFixDialog(List<FixItemRow> rows)
    {
        _rows = rows;
        InitializeComponent();
        Loaded += (_, _) => Initialize();
    }

    private void Initialize()
    {
        ItemsList.ItemsSource = _rows;
        ItemsList.DisplayMemberBinding = new Avalonia.Data.Binding("Display");
        if (ItemsList.ItemCount > 0)
            ItemsList.SelectedIndex = 0;

        GoToButton.Click += (_, _) => Confirm();
        CloseButton.Click += (_, _) => Close(null);
        ItemsList.DoubleTapped += (_, _) => Confirm();
    }

    private void Confirm()
    {
        if (ItemsList.SelectedItem is FixItemRow row)
            Close(row.ManufacturerId);
    }
}
