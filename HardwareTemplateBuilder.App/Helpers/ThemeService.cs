using Avalonia;
using Avalonia.Styling;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.App.Helpers;

/// <summary>
/// Applies and persists the app's Light/Dark appearance setting. The setting is stored via
/// <see cref="AppSettingRepository"/> under the <c>ThemeVariant</c> key, with "Dark" as the
/// default (matching the app's original always-dark look).
/// </summary>
public static class ThemeService
{
    public const string SettingKey = "ThemeVariant";
    public const string Light = "Light";
    public const string Dark = "Dark";

    /// <summary>Applies the given variant ("Light" or "Dark", case-insensitive) to the running app.</summary>
    public static void Apply(string? variant)
    {
        var themeVariant = string.Equals(variant, Light, System.StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = themeVariant;
    }

    /// <summary>Reads the saved variant from the database, applying it if found (defaults to Dark).</summary>
    public static void ApplySaved()
    {
        try
        {
            using var ctx = DatabaseInitializer.CreateContext();
            var repo = new AppSettingRepository(ctx);
            var saved = repo.GetValue(SettingKey);
            Apply(string.IsNullOrWhiteSpace(saved) ? Dark : saved);
        }
        catch
        {
            Apply(Dark);
        }
    }

    /// <summary>Persists the given variant and applies it immediately.</summary>
    public static void SetAndApply(AppSettingRepository repo, string variant)
    {
        var normalized = string.Equals(variant, Light, System.StringComparison.OrdinalIgnoreCase) ? Light : Dark;
        repo.SetValue(SettingKey, normalized);
        Apply(normalized);
    }
}
