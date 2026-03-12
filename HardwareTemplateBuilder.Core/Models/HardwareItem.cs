using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a specific hardware product (manufacturer + description + model number).
/// Frequency tracks how often this item is used in lookups and job packages.
/// </summary>
public class HardwareItem
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the foreign key to the manufacturer.</summary>
    public int ManufacturerId { get; set; }

    /// <summary>Gets or sets the manufacturer of this hardware item.</summary>
    public Manufacturer Manufacturer { get; set; } = null!;

    /// <summary>Gets or sets the foreign key to the description.</summary>
    public int DescriptionId { get; set; }

    /// <summary>Gets or sets the description category of this hardware item.</summary>
    public Description Description { get; set; } = null!;

    /// <summary>Gets or sets the model number of the hardware item.</summary>
    [Required]
    public string ModelNumber { get; set; } = string.Empty;

    /// <summary>Gets or sets optional remarks or notes about this hardware item.</summary>
    public string? Remarks { get; set; }

    /// <summary>
    /// Gets or sets the usage frequency counter.
    /// Incremented each time this item is retrieved via lookup or included in a job.
    /// Used to weight search results (higher = shown first).
    /// </summary>
    public int Frequency { get; set; }

    /// <summary>Gets or sets the junction records linking this item to its templates.</summary>
    public ICollection<HardwareItemTemplate> HardwareItemTemplates { get; set; } = new List<HardwareItemTemplate>();

    /// <summary>Gets or sets the junction records linking this item to jobs.</summary>
    public ICollection<JobHardware> JobHardwareLinks { get; set; } = new List<JobHardware>();
}
