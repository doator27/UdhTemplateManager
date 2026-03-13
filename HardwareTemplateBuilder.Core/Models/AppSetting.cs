using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a global application configuration key-value pair stored in the database.
/// </summary>
public class AppSetting
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the unique setting name (e.g. <c>"TemplateStorageLocation"</c>).</summary>
    [Required]
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the setting value.</summary>
    public string Value { get; set; } = string.Empty;
}
