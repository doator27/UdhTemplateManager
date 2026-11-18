namespace HardwareTemplateBuilder.Core.Models;

/// <summary>The text of one page of a <see cref="ProductCatalog"/>.</summary>
public class ProductCatalogPage
{
    /// <summary>Gets or sets the primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the catalog.</summary>
    public int CatalogId { get; set; }

    /// <summary>Gets or sets the catalog (deleting it deletes its pages).</summary>
    public ProductCatalog Catalog { get; set; } = null!;

    /// <summary>Gets or sets the 1-based page number.</summary>
    public int PageNumber { get; set; }

    /// <summary>Gets or sets the page text, one layout line per line (columns separated by two spaces).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the page's comparison key (see <c>ImportText.Key</c>), searched with LIKE.</summary>
    public string SearchKey { get; set; } = string.Empty;
}
