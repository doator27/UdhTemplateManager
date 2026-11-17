using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>
/// Text helpers shared by the hardware PDF parsers and the matcher: comparison keys that ignore
/// spacing/punctuation (and the usual OCR O/0 and I/1 confusions), size suffixes, handing and
/// finish codes.
/// </summary>
public static class ImportText
{
    /// <summary>Door handing codes that are stripped from a catalog number.</summary>
    public static readonly HashSet<string> Handings = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "LH", "RH", "LHR", "RHR", "LHRA", "RHRA", "LHA", "RHA", "LHRB", "RHRB", "LHB", "RHB", "RHRH", "LHRH",
    };

    private static readonly Regex StandardFinish = new(
        @"^(US\d{1,2}[A-Z]?|\d{3}(AN)?|AL|BLK|BLACK|BN|CRM|SP\d+|SPBLK|SSL|GRY|TBD|S2|26D?|10B?|BRNZ|BRZ|DKB|SN|PN|ORB|MB|SB|PB|SSS|US\d+D?/\d+|[A-Z]{2,4}\d?/\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SizeSuffix = new(
        @"(\s+(X\s+)?\d+(\s+\d+/\d+)?\s*[""'”’″]+)+\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Comparison key: upper-case letters and digits only, with O read as 0 and I as 1 so the
    /// same callout matches whether it came from PDF text or from OCR.
    /// "5BB1 4 1/2 x 4 1/2" and "5BB1 4-1/2 x 4-1/2" give the same key.
    /// </summary>
    /// <param name="text">Any text (catalog number, model number, label).</param>
    public static string Key(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var c = char.ToUpperInvariant(raw);
            if (c == 'O') c = '0';
            else if (c == 'I') c = '1';
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Removes trailing sizes such as 36", 18', X 36" or 2 x 124" from a catalog number.</summary>
    /// <param name="catalog">The catalog text.</param>
    public static string StripSize(string catalog) => SizeSuffix.Replace(catalog, string.Empty).Trim();

    /// <summary>True when a token is a finish code (from the PDF's finish list or a standard one).</summary>
    /// <param name="token">One column value.</param>
    /// <param name="pdfFinishes">Finish codes listed in the PDF, if any.</param>
    public static bool IsFinish(string token, ISet<string>? pdfFinishes) =>
        (pdfFinishes != null && pdfFinishes.Contains(token)) || StandardFinish.IsMatch(token);

    /// <summary>
    /// Cleans a manufacturer code from a schedule, fixing common OCR mistakes:
    /// "$501" → "S501", "MKO1" → "MK01", "Lc" → "LC".
    /// </summary>
    /// <param name="code">The code as read.</param>
    public static string CleanManufacturerCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var c = code.Trim().ToUpperInvariant().Replace('$', 'S');
        var m = Regex.Match(c, @"^([A-Z]{1,3}?)([0-9OIL]{2,})$");
        if (m.Success && m.Groups[2].Value.Any(char.IsDigit))
            c = m.Groups[1].Value + m.Groups[2].Value.Replace('O', '0').Replace('I', '1').Replace('L', '1');
        return c;
    }

    /// <summary>Collapses runs of whitespace and normalises curly quotes.</summary>
    /// <param name="text">Text to tidy.</param>
    public static string Tidy(string text) =>
        Regex.Replace(text.Replace('“', '"').Replace('”', '"').Replace('″', '"').Replace('’', '\'').Replace('‘', '\''), @"\s+", " ").Trim();
}
