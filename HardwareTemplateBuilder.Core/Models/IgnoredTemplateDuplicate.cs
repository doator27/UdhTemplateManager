using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a template duplicate group that the user has chosen to ignore.
/// When templates share a link but need to exist separately (e.g., different page ranges),
/// the user can mark the duplicate as "ignored" to exclude it from future scans.
/// </summary>
public class IgnoredTemplateDuplicate
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the shared link that creates the duplicate group.
    /// This is the OnlineLink or LocalLink value that multiple templates share.
    /// </summary>
    [Required]
    public string SharedLink { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the link type: "Online" or "Local".
    /// </summary>
    [Required]
    public string LinkType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC date and time when this duplicate was marked as ignored.
    /// </summary>
    public DateTime IgnoredAt { get; set; } = DateTime.UtcNow;
}
