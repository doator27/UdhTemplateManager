using System;
using System.Windows;
using System.Windows.Controls;

namespace HardwareTemplateBuilder.Wpf.Ui;

/// <summary>
/// Code-built item templates: WPF's stand-in for Avalonia's <c>FuncDataTemplate</c>. Several
/// views build each list row in code (a checkbox wired to a HashSet, a row of buttons, ...);
/// <see cref="SetItemFactory{T}"/> keeps that code unchanged.
/// </summary>
public static class ItemFactory
{
    /// <summary>
    /// The row builder, set on the ItemsControl and inherited down to each row's
    /// <see cref="FactoryHost"/>.
    /// </summary>
    public static readonly DependencyProperty FactoryProperty = DependencyProperty.RegisterAttached(
        "Factory", typeof(Func<object, UIElement?>), typeof(ItemFactory),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>Gets the inherited row builder.</summary>
    public static Func<object, UIElement?>? GetFactory(DependencyObject d) => (Func<object, UIElement?>?)d.GetValue(FactoryProperty);

    /// <summary>Sets the row builder.</summary>
    public static void SetFactory(DependencyObject d, Func<object, UIElement?>? value) => d.SetValue(FactoryProperty, value);

    /// <summary>
    /// Builds each item of <paramref name="itemsControl"/> with <paramref name="factory"/>.
    /// Rows are rebuilt whenever a container is reused for a different item, so code-held
    /// state (e.g. a checked HashSet) can never desync from what is shown.
    /// </summary>
    public static void SetItemFactory<T>(this ItemsControl itemsControl, Func<T, UIElement?> factory)
    {
        SetFactory(itemsControl, item => item is T typed ? factory(typed) : new TextBlock { Text = item?.ToString() });
        itemsControl.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(FactoryHost)) };
    }
}

/// <summary>Hosts one code-built row; rebuilds when its item (DataContext) or the factory changes.</summary>
public sealed class FactoryHost : ContentControl
{
    /// <summary>Initializes a non-focusable host.</summary>
    public FactoryHost()
    {
        Focusable = false;
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == DataContextProperty || e.Property == ItemFactory.FactoryProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        var factory = ItemFactory.GetFactory(this);
        Content = factory != null && DataContext != null ? factory(DataContext) : null;
    }
}
