using Avalonia.Controls;
using System.Threading.Tasks;

namespace HardwareTemplateBuilder.App.Helpers;

/// <summary>
/// Provides helper methods for showing common dialogs.
/// </summary>
public static class DialogHelper
{
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
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Avalonia.Media.Brushes.Silver,
        };

        var result = false;

        var yesButton = new Button { Content = "Yes", Width = 80, Margin = new Avalonia.Thickness(5) };
        var noButton = new Button { Content = "No", Width = 80, Margin = new Avalonia.Thickness(5) };

        yesButton.Click += (_, _) => { result = true; dialog.Close(); };
        noButton.Click += (_, _) => { result = false; dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(0, 0, 0, 16)
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Children = { yesButton, noButton }
                }
            }
        };

        await dialog.ShowDialog(owner);
        return result;
    }
}
