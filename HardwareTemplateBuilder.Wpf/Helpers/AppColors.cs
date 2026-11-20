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

    /// <summary>Bulk Add item card background (odd rows): the Win95 face gray.</summary>
    public static Brush CardBackground { get; } = UiExtensions.Brush("#C0C0C0");

    /// <summary>Bulk Add item card background (even rows), so neighbouring cards stand apart.</summary>
    public static Brush CardAltBackground { get; } = UiExtensions.Brush("#D8D8D8");

    /// <summary>Bulk Add item card background under the mouse.</summary>
    public static Brush CardHoverBackground { get; } = UiExtensions.Brush("#C8D0E8");

    /// <summary>Bulk Add item card outline.</summary>
    public static Brush CardBorder { get; } = UiExtensions.Brush("#808080");

    /// <summary>Outline of the expanded (being edited) Bulk Add item card (Win95 navy).</summary>
    public static Brush CardActiveBorder { get; } = UiExtensions.Brush("#000080");
}
