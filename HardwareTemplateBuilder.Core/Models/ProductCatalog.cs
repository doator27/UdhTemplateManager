using System;

namespace HardwareTemplateBuilder.Core.Models;

/// <summary>
/// A manufacturer's product catalog PDF (a local file and/or an online link). Its text is read
/// into <see cref="ProductCatalogPage"/> rows so the template lookups can find a callout's
/// catalog page: the template number it lists, what the option codes mean, and the product
/// description. Managed in Maintenance → Product Catalogs.
/// </summary>
public class ProductCatalog
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the manufacturer (plain id; catalogs of a deleted manufacturer are ignored).</summary>
    public int ManufacturerId { get; set; }

    /// <summary>Gets or sets the display title, e.g. "LCN Closers Price Book 2026".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets where the catalog was downloaded from, if online.</summary>
    public string? OnlineLink { get; set; }

    /// <summary>Gets or sets the local copy (the user's own file, or the downloaded copy).</summary>
    public string? LocalLink { get; set; }

    /// <summary>Gets or sets the number of pages read.</summary>
    public int PageCount { get; set; }

    /// <summary>Gets or sets when it was added (UTC).</summary>
    public DateTime AddedUtc { get; set; }

    /// <summary>Gets or sets when its text was last read (UTC); null = not read yet.</summary>
    public DateTime? IndexedUtc { get; set; }

    /// <summary>Gets or sets why the last read failed, if it did.</summary>
    public string? LastError { get; set; }
}
