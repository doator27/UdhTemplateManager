using System.Windows;

namespace HardwareTemplateBuilder.Wpf.Ui;

/// <summary>
/// Placeholder text for TextBox and ComboBox (Avalonia's <c>Watermark</c> /
/// <c>PlaceholderText</c>). The Win95 control templates draw it in gray while the field is empty.
/// </summary>
public static class Watermark
{
    /// <summary>The placeholder text.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Watermark), new FrameworkPropertyMetadata(null));

    /// <summary>Gets the placeholder text.</summary>
    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);

    /// <summary>Sets the placeholder text.</summary>
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);
}
