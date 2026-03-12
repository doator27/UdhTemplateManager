using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a hardware manufacturer.
/// </summary>
public class Manufacturer
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the manufacturer's name.</summary>
    [Required]
    public string ManufacturerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the hardware items from this manufacturer.</summary>
    public ICollection<HardwareItem> HardwareItems { get; set; } = new List<HardwareItem>();

    /// <summary>Gets or sets the templates from this manufacturer.</summary>
    public ICollection<IndividualTemplate> Templates { get; set; } = new List<IndividualTemplate>();
}
