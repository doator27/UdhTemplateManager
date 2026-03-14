using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a node in the user-defined description hierarchy.
/// Nodes can nest to any depth; sort order is relative to siblings under the same parent.
/// </summary>
public class Description
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the display name of this description node.</summary>
    [Required]
    public string DescriptionText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the parent node ID, or <c>null</c> for a root-level node.
    /// </summary>
    public int? ParentId { get; set; }

    /// <summary>Gets or sets the parent node navigation property.</summary>
    public Description? Parent { get; set; }

    /// <summary>Gets or sets the child nodes of this description.</summary>
    public ICollection<Description> Children { get; set; } = new List<Description>();

    /// <summary>
    /// Gets or sets the sort position among siblings (nodes sharing the same parent).
    /// Lower values appear first; equal values sort alphabetically by <see cref="DescriptionText"/>.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>Gets or sets the hardware items with this description.</summary>
    public ICollection<HardwareItem> HardwareItems { get; set; } = new List<HardwareItem>();

    /// <summary>Gets or sets the templates with this description.</summary>
    public ICollection<IndividualTemplate> Templates { get; set; } = new List<IndividualTemplate>();
}
