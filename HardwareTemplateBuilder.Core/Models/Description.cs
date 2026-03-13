using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a hardware description category (e.g., "Mortise Lockset").
/// Each description carries a manual <see cref="SortOrder"/> that determines its position
/// in assembled PDF packages. Lower values appear first; equal values sort alphabetically.
/// </summary>
public class Description
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the description text.</summary>
    [Required]
    public string DescriptionText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the manual sort position within assembled PDF packages.
    /// Lower values appear first. Descriptions with equal values are ordered alphabetically.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>Gets or sets the hardware items with this description.</summary>
    public ICollection<HardwareItem> HardwareItems { get; set; } = new List<HardwareItem>();

    /// <summary>Gets or sets the templates with this description.</summary>
    public ICollection<IndividualTemplate> Templates { get; set; } = new List<IndividualTemplate>();
}
