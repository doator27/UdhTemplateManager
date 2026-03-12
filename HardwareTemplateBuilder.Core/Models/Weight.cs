using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a sorting weight in the format "00.000.000" (Function.Type.SubType).
/// Used to order templates within a PDF package.
/// </summary>
public class Weight
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the weight string in "00.000.000" format.
    /// Example: "01.001.020" where 01=Hangs, 001=Mortise Lockset, 020=specific subtype.
    /// </summary>
    [Required]
    public string WeightValue { get; set; } = string.Empty;

    /// <summary>Gets or sets the foreign key to the associated description.</summary>
    public int DescriptionId { get; set; }

    /// <summary>Gets or sets the associated description.</summary>
    public Description Description { get; set; } = null!;

    /// <summary>Gets or sets the templates that use this weight for sorting.</summary>
    public ICollection<IndividualTemplate> Templates { get; set; } = new List<IndividualTemplate>();
}
