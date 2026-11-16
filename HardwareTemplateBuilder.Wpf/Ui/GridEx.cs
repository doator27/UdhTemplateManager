using System;
using System.Windows;
using System.Windows.Controls;

namespace HardwareTemplateBuilder.Wpf.Ui;

/// <summary>
/// Attached properties that let a <see cref="Grid"/> declare its rows and columns with the
/// compact comma-separated syntax Avalonia supports (<c>ui:GridEx.Columns="Auto,*,2*,120"</c>),
/// which keeps the ported XAML close to the Avalonia originals.
/// </summary>
public static class GridEx
{
    /// <summary>Comma-separated column widths, e.g. <c>"Auto,*,120"</c>.</summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.RegisterAttached(
        "Columns", typeof(string), typeof(GridEx), new PropertyMetadata(null, OnColumnsChanged));

    /// <summary>Comma-separated row heights, e.g. <c>"Auto,*"</c>.</summary>
    public static readonly DependencyProperty RowsProperty = DependencyProperty.RegisterAttached(
        "Rows", typeof(string), typeof(GridEx), new PropertyMetadata(null, OnRowsChanged));

    /// <summary>Gets the column spec.</summary>
    public static string? GetColumns(DependencyObject d) => (string?)d.GetValue(ColumnsProperty);

    /// <summary>Sets the column spec.</summary>
    public static void SetColumns(DependencyObject d, string? value) => d.SetValue(ColumnsProperty, value);

    /// <summary>Gets the row spec.</summary>
    public static string? GetRows(DependencyObject d) => (string?)d.GetValue(RowsProperty);

    /// <summary>Sets the row spec.</summary>
    public static void SetRows(DependencyObject d, string? value) => d.SetValue(RowsProperty, value);

    /// <summary>
    /// Builds a column-definition list from the compact spec, for code-built grids
    /// (<c>new Grid { ColumnDefinitions = ... }</c> in Avalonia).
    /// </summary>
    public static Grid Columns(this Grid grid, string spec)
    {
        SetColumns(grid, spec);
        return grid;
    }

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid) return;
        grid.ColumnDefinitions.Clear();
        foreach (var length in Parse(e.NewValue as string))
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = length });
    }

    private static void OnRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid) return;
        grid.RowDefinitions.Clear();
        foreach (var length in Parse(e.NewValue as string))
            grid.RowDefinitions.Add(new RowDefinition { Height = length });
    }

    private static GridLength[] Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return Array.Empty<GridLength>();
        var converter = new GridLengthConverter();
        var parts = spec.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new GridLength[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            result[i] = (GridLength)converter.ConvertFromInvariantString(parts[i])!;
        return result;
    }
}
