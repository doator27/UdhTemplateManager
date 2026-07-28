using Avalonia.Media;

namespace HardwareTemplateBuilder.App.Helpers;

/// <summary>
/// Shared status-text brushes for code-behind assignments (e.g. <c>StatusLabel.Foreground = ...</c>)
/// that can't be reached by XAML Styles. Kept in sync with the palette in
/// <c>Styles/AppTheme.axaml</c> — when the app theme changes, update both together.
/// </summary>
public static class AppColors
{
    /// <summary>Error/failure status text.</summary>
    public static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#FF6B6B"));

    /// <summary>Success/completion status text.</summary>
    public static readonly IBrush Success = new SolidColorBrush(Color.Parse("#5FD68C"));

    /// <summary>Muted/secondary helper text.</summary>
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#A7ADB8"));

    /// <summary>Primary body text, for places that previously hardcoded pure black.</summary>
    public static readonly IBrush Primary = new SolidColorBrush(Color.Parse("#E8EAED"));

    /// <summary>Informational/neutral-emphasis text.</summary>
    public static readonly IBrush Info = new SolidColorBrush(Color.Parse("#6FB6DB"));

    /// <summary>Warning status text.</summary>
    public static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#FFA94D"));
}
