using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Junction table linking hardware items to their associated individual templates.
/// </summary>
public class HardwareItemTemplate
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the foreign key to the hardware item.</summary>
    public int HardwareItemId { get; set; }

    /// <summary>Gets or sets the associated hardware item.</summary>
    public HardwareItem HardwareItem { get; set; } = null!;

    /// <summary>Gets or sets the foreign key to the individual template.</summary>
    public int IndividualTemplateId { get; set; }

    /// <summary>Gets or sets the associated individual template.</summary>
    public IndividualTemplate IndividualTemplate { get; set; } = null!;
}
