using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a hardware description category (e.g., "Mortise Lockset").
/// Each description carries a sort weight in <c>00.000.000</c> format that
/// determines its position in assembled PDF packages.
/// </summary>
public class Description
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the description text.</summary>
    [Required]
    public string DescriptionText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sort weight in <c>00.000.000</c> format (e.g. <c>"01.002.015"</c>).
    /// Controls the order of this hardware type within an assembled PDF package.
    /// </summary>
    [Required]
    public string WeightValue { get; set; } = "00.000.000";

    /// <summary>Gets or sets the hardware items with this description.</summary>
    public ICollection<HardwareItem> HardwareItems { get; set; } = new List<HardwareItem>();

    /// <summary>Gets or sets the templates with this description.</summary>
    public ICollection<IndividualTemplate> Templates { get; set; } = new List<IndividualTemplate>();
}
