using Avalonia.Controls;
using System.Threading.Tasks;

namespace HardwareTemplateBuilder.App.Helpers;

/// <summary>
/// Provides helper methods for showing common dialogs.
/// </summary>
public static class DialogHelper
{
    /// <summary>
    /// Shows an informational dialog with an OK button.
    /// </summary>
    /// <param name="owner">The parent window.</param>
    /// <param name="message">The message to display.</param>
    /// <param name="title">The dialog title.</param>
    public static async Task ShowInfoAsync(Window owner, string message, string title = "Information")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            Height = 220,
            MinWidth = 300,
            MinHeight = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
        };

        var okButton = new Button { Content = "OK", Width = 80, Margin = new Avalonia.Thickness(5) };
        okButton.Click += (_, _) => dialog.Close();

        dialog.Content = new Avalonia.Controls.DockPanel
        {
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new StackPanel
                {
                    [Avalonia.Controls.DockPanel.DockProperty] = Avalonia.Controls.Dock.Bottom,
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Margin = new Avalonia.Thickness(0, 8, 0, 0),
                    Children = { okButton }
                },
                new Avalonia.Controls.ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Margin = new Avalonia.Thickness(0, 0, 0, 8)
                    }
                }
            }
        };

        await dialog.ShowDialog(owner);
    }

    /// <summary>
    /// Shows an informational dialog with a scrollable message area and an OK button.
    /// Use this instead of <see cref="ShowInfoAsync"/> when the message may be long.
    /// </summary>
    /// <param name="owner">The parent window.</param>
    /// <param name="message">The message to display (may be multi-line).</param>
    /// <param name="title">The dialog title.</param>
    public static async Task ShowScrollableInfoAsync(Window owner, string message, string title = "Information")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            Height = 420,
            MinWidth = 300,
            MinHeight = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
        };

        var okButton = new Button { Content = "OK", Width = 80, Margin = new Avalonia.Thickness(5) };
        okButton.Click += (_, _) => dialog.Close();

        dialog.Content = new Avalonia.Controls.DockPanel
        {
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new StackPanel
                {
                    [Avalonia.Controls.DockPanel.DockProperty] = Avalonia.Controls.Dock.Bottom,
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Margin = new Avalonia.Thickness(0, 8, 0, 0),
                    Children = { okButton }
                },
                new Avalonia.Controls.ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontFamily = new Avalonia.Media.FontFamily("Courier New, Consolas, monospace"),
                        FontSize = 12,
                    }
                }
            }
        };

        await dialog.ShowDialog(owner);
    }

    /// <summary>
    /// Shows a Yes/No confirmation dialog and returns true if the user confirms.
    /// </summary>
    /// <param name="owner">The parent window.</param>
    /// <param name="message">The confirmation message to display.</param>
    /// <param name="title">The dialog title.</param>
    /// <returns>True if the user clicked Yes; false otherwise.</returns>
    public static async Task<bool> ConfirmAsync(Window owner, string message, string title = "Confirm")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            Height = 180,
            MinWidth = 300,
            MinHeight = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
        };

        var result = false;

        var yesButton = new Button { Content = "Yes", Width = 80, Margin = new Avalonia.Thickness(5) };
        var noButton = new Button { Content = "No", Width = 80, Margin = new Avalonia.Thickness(5) };

        yesButton.Click += (_, _) => { result = true; dialog.Close(); };
        noButton.Click += (_, _) => { result = false; dialog.Close(); };

        dialog.Content = new Avalonia.Controls.DockPanel
        {
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new StackPanel
                {
                    [Avalonia.Controls.DockPanel.DockProperty] = Avalonia.Controls.Dock.Bottom,
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Margin = new Avalonia.Thickness(0, 8, 0, 0),
                    Children = { yesButton, noButton }
                },
                new Avalonia.Controls.ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Margin = new Avalonia.Thickness(0, 0, 0, 8)
                    }
                }
            }
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
