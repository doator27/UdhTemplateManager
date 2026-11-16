using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HardwareTemplateBuilder.Wpf.Ui;

namespace HardwareTemplateBuilder.Wpf.Helpers;

/// <summary>
/// Simple Win95-style message boxes built in code (no XAML), with the same method names and
/// signatures as the Avalonia app's <c>DialogHelper</c> so ported views call them unchanged.
/// </summary>
public static class DialogHelper
{
    /// <summary>The icon drawn at the left of a message box.</summary>
    private enum Glyph { Info, Question, Error }

    /// <summary>Shows an informational dialog with an OK button.</summary>
    /// <param name="owner">The parent window.</param>
    /// <param name="message">The message to display.</param>
    /// <param name="title">The dialog title.</param>
    public static Task ShowInfoAsync(Window? owner, string message, string title = "Information")
    {
        var dialog = CreateDialog(title, 440, 220);
        var ok = MakeButton("OK", isDefault: true, isCancel: true);
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = Layout(MessageArea(message, monospace: false, Glyph.Info), ok);
        return dialog.ShowDialogAsync(owner);
    }

    /// <summary>Shows an error dialog (red Win95 "X" icon) with an OK button.</summary>
    /// <param name="owner">The parent window, or null to center on screen.</param>
    /// <param name="message">The message to display.</param>
    /// <param name="title">The dialog title.</param>
    public static Task ShowErrorAsync(Window? owner, string message, string title = "Error")
    {
        var dialog = CreateDialog(title, 440, 220);
        var ok = MakeButton("OK", isDefault: true, isCancel: true);
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = Layout(MessageArea(message, monospace: false, Glyph.Error), ok);
        return dialog.ShowDialogAsync(owner);
    }

    /// <summary>
    /// Shows an informational dialog with a scrollable, monospaced message area and an OK
    /// button. Use instead of <see cref="ShowInfoAsync"/> when the message may be long.
    /// </summary>
    public static Task ShowScrollableInfoAsync(Window? owner, string message, string title = "Information")
    {
        var dialog = CreateDialog(title, 560, 420);
        var ok = MakeButton("OK", isDefault: true, isCancel: true);
        ok.Click += (_, _) => dialog.Close();

        var box = new TextBox
        {
            Text = message,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Courier New, Consolas"),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.White,
        };
        dialog.Content = Layout(box, ok);
        return dialog.ShowDialogAsync(owner);
    }

    /// <summary>Shows a Yes/No confirmation dialog and returns true if the user clicks Yes.</summary>
    public static async Task<bool> ConfirmAsync(Window? owner, string message, string title = "Confirm")
    {
        var dialog = CreateDialog(title, 400, 190);
        var yes = MakeButton("Yes", isDefault: true, isCancel: false);
        var no = MakeButton("No", isDefault: false, isCancel: true);
        yes.Click += (_, _) => dialog.CloseWith(true);
        no.Click += (_, _) => dialog.CloseWith(false);
        dialog.Content = Layout(MessageArea(message, monospace: false, Glyph.Question), yes, no);
        return await dialog.ShowDialogAsync<bool>(owner);
    }

    /// <summary>
    /// Shows a dialog with a text box and OK/Cancel buttons; returns the entered text, or null
    /// if the user cancelled.
    /// </summary>
    public static async Task<string?> PromptAsync(Window? owner, string message, string title = "Input", string? defaultValue = null)
    {
        var dialog = CreateDialog(title, 400, 190);
        var textBox = new TextBox { Text = defaultValue ?? string.Empty, Margin = new Thickness(0, 8, 0, 0) };
        var ok = MakeButton("OK", isDefault: true, isCancel: false);
        var cancel = MakeButton("Cancel", isDefault: false, isCancel: true);
        ok.Click += (_, _) => dialog.CloseWith(textBox.Text);
        cancel.Click += (_, _) => dialog.CloseWith(null);

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(textBox);
        dialog.Content = Layout(body, ok, cancel);
        dialog.Loaded += (_, _) => { textBox.Focus(); textBox.SelectAll(); };

        return await dialog.ShowDialogAsync<string>(owner);
    }

    private static Win95Window CreateDialog(string title, double width, double height) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        MinWidth = 300,
        MinHeight = 150,
        ResizeMode = ResizeMode.CanResizeWithGrip,
    };

    private static Button MakeButton(string text, bool isDefault, bool isCancel)
    {
        var button = new Button
        {
            Content = text,
            Width = 80,
            Margin = new Thickness(4, 0, 4, 0),
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        if (isDefault) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        return button;
    }

    private static UIElement Layout(UIElement body, params Button[] buttons)
    {
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        };
        foreach (var b in buttons) buttonRow.Children.Add(b);

        var dock = new DockPanel { Margin = new Thickness(12), LastChildFill = true };
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        dock.Children.Add(buttonRow);
        dock.Children.Add(body);
        return dock;
    }

    private static UIElement MessageArea(string message, bool monospace, Glyph glyph)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        if (monospace) text.FontFamily = new FontFamily("Courier New, Consolas");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = MakeIcon(glyph);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var scroll = new ScrollViewer
        {
            Content = text,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        return grid;
    }

    /// <summary>The classic round "i" (white on blue), "?" (white balloon) or error "X" (white on red) icon.</summary>
    private static FrameworkElement MakeIcon(Glyph glyph)
    {
        if (glyph == Glyph.Error)
        {
            return new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Background = UiExtensions.Brush("#FF0000"),
                BorderBrush = UiExtensions.Brush("#800000"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M0,2 L2,0 L7,5 L12,0 L14,2 L9,7 L14,12 L12,14 L7,9 L2,14 L0,12 L5,7 Z"),
                    Fill = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        var circle = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = glyph == Glyph.Info ? UiExtensions.Brush("#0000FF") : UiExtensions.Brush("#FFFFFF"),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = glyph == Glyph.Info ? "i" : "?",
                FontFamily = new FontFamily("Times New Roman"),
                FontWeight = FontWeights.Bold,
                FontSize = 22,
                Foreground = glyph == Glyph.Info ? Brushes.White : UiExtensions.Brush("#0000FF"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        return circle;
    }
}
