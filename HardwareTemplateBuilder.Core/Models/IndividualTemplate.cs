using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// Represents a single installation template PDF with its page and rotation metadata.
/// </summary>
public class IndividualTemplate
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the foreign key to the manufacturer.</summary>
    public int ManufacturerId { get; set; }

    /// <summary>Gets or sets the manufacturer of this template.</summary>
    public Manufacturer Manufacturer { get; set; } = null!;

    /// <summary>Gets or sets the foreign key to the description.</summary>
    public int DescriptionId { get; set; }

    /// <summary>Gets or sets the description category of this template.</summary>
    public Description Description { get; set; } = null!;

    /// <summary>Gets or sets the template identifier number (e.g., "A-123").</summary>
    [Required]
    public string TemplateNumber { get; set; } = string.Empty;

    /// <summary>Gets or sets the total page count of the source PDF.</summary>
    public int NumPages { get; set; }

    /// <summary>
    /// Gets or sets the pages to include when extracting from the source PDF.
    /// Supports: single ("1"), range ("2-7"), non-consecutive ("3,6,8"), or combinations ("1,3-5,8").
    /// </summary>
    [Required]
    public string PagesToPrint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the pages to rotate within the extracted set.
    /// Uses the same format as PagesToPrint. Null if no rotation is needed.
    /// </summary>
    public string? PagesToRotate { get; set; }

    /// <summary>
    /// Gets or sets the rotation angle in degrees.
    /// Positive = clockwise, Negative = counterclockwise.
    /// Applied uniformly to all pages listed in PagesToRotate.
    /// </summary>
    public int RotationDirection { get; set; }

    /// <summary>Gets or sets the foreign key to the door material.</summary>
    public int DoorMaterialId { get; set; }

    /// <summary>Gets or sets the door material this template applies to.</summary>
    public DoorMaterial DoorMaterial { get; set; } = null!;

    /// <summary>Gets or sets the URL to the source PDF file online.</summary>
    public string? OnlineLink { get; set; }

    /// <summary>Gets or sets the local filesystem path to a cached copy of the PDF.</summary>
    public string? LocalLink { get; set; }

    /// <summary>
    /// Gets or sets the optional foreign key to the job that originally created this template.
    /// Null means the template is globally visible; non-null means it is hidden from other jobs' searches.
    /// </summary>
    public int? OriginJobId { get; set; }

    /// <summary>Gets or sets the job that originally created this template (null = global).</summary>
    public Job? OriginJob { get; set; }

    /// <summary>Gets or sets the junction records linking this template to hardware items.</summary>
    public ICollection<HardwareItemTemplate> HardwareItemTemplates { get; set; } = new List<HardwareItemTemplate>();

    /// <summary>Gets or sets the snapshots created when this template was used in job packages.</summary>
    public ICollection<JobTemplateSnapshot> Snapshots { get; set; } = new List<JobTemplateSnapshot>();
}
