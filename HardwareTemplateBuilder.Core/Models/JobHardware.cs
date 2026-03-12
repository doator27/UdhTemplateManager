using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Junction table linking hardware items to jobs.
/// </summary>
public class JobHardware
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the foreign key to the job.</summary>
    public int JobId { get; set; }

    /// <summary>Gets or sets the associated job.</summary>
    public Job Job { get; set; } = null!;

    /// <summary>Gets or sets the foreign key to the hardware item.</summary>
    public int HardwareItemId { get; set; }

    /// <summary>Gets or sets the associated hardware item.</summary>
    public HardwareItem HardwareItem { get; set; } = null!;
}
