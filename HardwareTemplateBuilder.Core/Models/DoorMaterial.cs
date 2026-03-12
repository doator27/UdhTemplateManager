using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a door material type. Only "Hollow Metal" or "Wood" are valid values.
/// </summary>
public class DoorMaterial
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the material name. Must be either "Hollow Metal" or "Wood".
    /// </summary>
    [Required]
    public string Material { get; set; } = string.Empty;

    /// <summary>Gets or sets the templates associated with this door material.</summary>
    public ICollection<IndividualTemplate> Templates { get; set; } = new List<IndividualTemplate>();
}
