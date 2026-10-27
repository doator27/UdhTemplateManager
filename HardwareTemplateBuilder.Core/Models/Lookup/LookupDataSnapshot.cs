using System;
using System.Collections.Generic;

namespace HardwareTemplateBuilder.Core.Models.Lookup;

/// <summary>
/// Serializable snapshot of the metadata needed by the standalone Lookup app to search
/// hardware items and merge their linked templates without a direct database connection.
/// Exported by <c>LookupDataExportService</c> whenever "Refresh Templates" completes in the
/// main app, and read by <c>HardwareTemplateBuilder.Lookup</c> at startup.
/// </summary>
public class LookupDataSnapshot
{
    /// <summary>Gets or sets the UTC timestamp the snapshot was generated at.</summary>
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>Gets or sets the configured template storage location at export time.</summary>
    public string TemplateStorageLocation { get; set; } = string.Empty;

    /// <summary>Gets or sets every manufacturer.</summary>
    public List<LookupManufacturer> Manufacturers { get; set; } = new();

    /// <summary>Gets or sets every description node (including non-leaf/inactive, for hierarchy resolution).</summary>
    public List<LookupDescription> Descriptions { get; set; } = new();

    /// <summary>Gets or sets every active hardware item.</summary>
    public List<LookupHardwareItem> HardwareItems { get; set; } = new();

    /// <summary>Gets or sets every individual template.</summary>
    public List<LookupIndividualTemplate> Templates { get; set; } = new();

    /// <summary>Gets or sets every hardware item to template link (global, i.e. JobId == null).</summary>
    public List<LookupHardwareItemTemplate> HardwareItemTemplates { get; set; } = new();

    /// <summary>
    /// Gets or sets every distinct custom label that has been used for a hardware item on any
    /// job, mapped to the hardware item it was used for. Used by the "look up by label" search.
    /// </summary>
    public List<LookupJobHardwareLabel> Labels { get; set; } = new();
}

/// <summary>Flattened manufacturer record.</summary>
public class LookupManufacturer
{
    public int Id { get; set; }
    public string ManufacturerName { get; set; } = string.Empty;
}

/// <summary>Flattened description record.</summary>
public class LookupDescription
{
    public int Id { get; set; }
    public string DescriptionText { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>Flattened hardware item record.</summary>
public class LookupHardwareItem
{
    public int Id { get; set; }
    public int ManufacturerId { get; set; }
    public int DescriptionId { get; set; }
    public string ModelNumber { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int Frequency { get; set; }
}

/// <summary>Flattened individual template record.</summary>
public class LookupIndividualTemplate
{
    public int Id { get; set; }
    public int ManufacturerId { get; set; }
    public int DescriptionId { get; set; }
    public string TemplateNumber { get; set; } = string.Empty;
    public int NumPages { get; set; }
    public string PagesToPrint { get; set; } = string.Empty;
    public string? PagesToRotate { get; set; }
    public int RotationDirection { get; set; }
    public string? OnlineLink { get; set; }
    public string? LocalLink { get; set; }
}

/// <summary>Flattened hardware item to template link record.</summary>
public class LookupHardwareItemTemplate
{
    public int HardwareItemId { get; set; }
    public int IndividualTemplateId { get; set; }
}

/// <summary>A distinct custom label used for a hardware item on a job, and the item it refers to.</summary>
public class LookupJobHardwareLabel
{
    public string Label { get; set; } = string.Empty;
    public int HardwareItemId { get; set; }
}
