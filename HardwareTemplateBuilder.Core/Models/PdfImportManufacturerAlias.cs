using System;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// A manufacturer code seen in hardware PDFs (e.g. "IV", "MK01", "NA") and how many times the user
/// confirmed it as a given manufacturer in the PDF import wizard. Used once the count reaches the
/// auto-match threshold and clearly outweighs any other manufacturer for the same code.
/// </summary>
public class PdfImportManufacturerAlias
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the code (cleaned, upper case).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the manufacturer it was confirmed as.</summary>
    public int ManufacturerId { get; set; }

    /// <summary>Gets or sets how many times it was confirmed.</summary>
    public int ConfirmCount { get; set; }

    /// <summary>Gets or sets when it was last confirmed (UTC).</summary>
    public DateTime LastConfirmedUtc { get; set; }
}
