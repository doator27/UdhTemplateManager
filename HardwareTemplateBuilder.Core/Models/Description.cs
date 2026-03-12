using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a hardware description category (e.g., "Mortise Lockset").
/// </summary>
public class Description
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the description text.</summary>
    [Required]
    public string DescriptionText { get; set; } = string.Empty;

    /// <summary>Gets or sets the weights associated with this description.</summary>
    public ICollection<Weight> Weights { get; set; } = new List<Weight>();

    /// <summary>Gets or sets the hardware items with this description.</summary>
    public ICollection<HardwareItem> HardwareItems { get; set; } = new List<HardwareItem>();

    /// <summary>Gets or sets the templates with this description.</summary>
    public ICollection<IndividualTemplate> Templates { get; set; } = new List<IndividualTemplate>();
}
