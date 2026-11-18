namespace HardwareTemplateBuilder.Core.Services.TemplateFinder;

/// <summary>A PDF link the template finder found, after it was downloaded and checked.</summary>
public sealed class TemplateCandidate
{
    /// <summary>Gets or sets the link.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets where it came from ("Your past links", "Manufacturer website", "Internet Archive copy", "Pasted link").</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the link downloaded as a real PDF.</summary>
    public bool IsPdf { get; set; }

    /// <summary>Gets or sets the PDF's page count (0 if not a PDF).</summary>
    public int PageCount { get; set; }

    /// <summary>Gets or sets whether the PDF's text mentions one of the search terms.</summary>
    public bool MentionsTerm { get; set; }

    /// <summary>Gets or sets which term it mentions, for display.</summary>
    public string? MatchedTerm { get; set; }

    /// <summary>Gets or sets whether the PDF's text looks like a template/installation sheet.</summary>
    public bool LooksLikeTemplate { get; set; }

    /// <summary>Gets or sets the ranking score (higher first).</summary>
    public int Score { get; set; }

    /// <summary>Gets or sets the PDF bytes for the preview (null when not a PDF).</summary>
    public byte[]? PdfBytes { get; set; }

    /// <summary>Gets or sets why it failed, when it isn't a usable PDF.</summary>
    public string? Error { get; set; }

    /// <summary>The file name part of the link, for display.</summary>
    public string FileName
    {
        get
        {
            try
            {
                var path = new System.Uri(Url).AbsolutePath;
                var name = System.Uri.UnescapeDataString(path.Substring(path.LastIndexOf('/') + 1));
                return name.Length > 0 ? name : Url;
            }
            catch
            {
                return Url;
            }
        }
    }

    /// <summary>List text: "★ 613_cutsheet.pdf  (2 pages, mentions 613)  - Your past links".</summary>
    public string DisplayText =>
        !IsPdf
            ? $"   {FileName}  ({Error ?? "not a PDF"})  - {Source}"
            : $"{(MentionsTerm ? "* " : "   ")}{FileName}  ({PageCount} page{(PageCount == 1 ? "" : "s")}{(MentionsTerm ? $", mentions {MatchedTerm}" : string.Empty)}{(LooksLikeTemplate ? ", template" : string.Empty)})  - {Source}";

    /// <inheritdoc />
    public override string ToString() => DisplayText;
}
