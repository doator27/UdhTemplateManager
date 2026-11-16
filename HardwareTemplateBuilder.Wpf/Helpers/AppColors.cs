using System.Windows.Media;
using HardwareTemplateBuilder.Wpf.Ui;

namespace HardwareTemplateBuilder.Wpf.Helpers;

/// <summary>
/// Status-text brushes for code-behind assignments (e.g. <c>StatusLabel.Foreground = ...</c>),
/// in the Windows 95 16-color palette so they read well on the gray #C0C0C0 face.
/// Same member names as the Avalonia app's <c>AppColors</c>.
/// </summary>
public static class AppColors
{
    /// <summary>Error/failure status text (dark red).</summary>
    public static Brush Danger { get; } = UiExtensions.Brush("#A00000");

    /// <summary>Success/completion status text (dark green).</summary>
    public static Brush Success { get; } = UiExtensions.Brush("#006400");

    /// <summary>Muted/secondary helper text.</summary>
    public static Brush Muted { get; } = UiExtensions.Brush("#404040");

    /// <summary>Primary body text.</summary>
    public static Brush Primary { get; } = UiExtensions.Brush("#000000");

    /// <summary>Informational/neutral-emphasis text (navy).</summary>
    public static Brush Info { get; } = UiExtensions.Brush("#000080");

    /// <summary>Warning status text (olive/brown).</summary>
    public static Brush Warning { get; } = UiExtensions.Brush("#806000");
}
