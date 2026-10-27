using Avalonia;
using Avalonia.Media;

namespace HardwareTemplateBuilder.App.Helpers;

/// <summary>
/// Shared status-text brushes for code-behind assignments (e.g. <c>StatusLabel.Foreground = ...</c>)
/// that can't be reached by XAML Styles. Resolved from the active theme's App.axaml
/// ThemeDictionary resources (AppDanger/AppSuccess/AppMuted/etc.) so they automatically follow
/// the Light/Dark selection made in App Settings; falls back to the dark-theme literal if the
/// resource isn't found (e.g. in design mode).
/// </summary>
public static class AppColors
{
    private static IBrush Resolve(string key, string fallbackHex)
    {
        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            && value is IBrush brush)
            return brush;

        return new SolidColorBrush(Color.Parse(fallbackHex));
    }

    /// <summary>Error/failure status text.</summary>
    public static IBrush Danger => Resolve("AppDanger", "#FF6B6B");

    /// <summary>Success/completion status text.</summary>
    public static IBrush Success => Resolve("AppSuccess", "#5FD68C");

    /// <summary>Muted/secondary helper text.</summary>
    public static IBrush Muted => Resolve("AppTextSecondary", "#A7ADB8");

    /// <summary>Primary body text, for places that previously hardcoded pure black.</summary>
    public static IBrush Primary => Resolve("AppTextPrimary", "#E8EAED");

    /// <summary>Informational/neutral-emphasis text.</summary>
    public static IBrush Info => Resolve("AppInfo", "#6FB6DB");

    /// <summary>Warning status text.</summary>
    public static IBrush Warning => Resolve("AppWarning", "#FFA94D");
}

