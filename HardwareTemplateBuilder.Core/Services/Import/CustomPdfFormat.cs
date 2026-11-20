using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HardwareTemplateBuilder.Core.Data;
using HardwareTemplateBuilder.Core.Repositories;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>What a column of a taught PDF format holds.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PdfFormatField
{
    /// <summary>Not used.</summary>
    Ignore = 0,

    /// <summary>The quantity.</summary>
    Quantity,

    /// <summary>The manufacturer (a name or a code).</summary>
    Manufacturer,

    /// <summary>The model / catalog / product number (the callout).</summary>
    Catalog,

    /// <summary>The finish.</summary>
    Finish,

    /// <summary>The item's description (generic name).</summary>
    Description,

    /// <summary>The handing.</summary>
    Handing,
}

/// <summary>One column of a taught PDF format.</summary>
public sealed class PdfFormatColumn
{
    /// <summary>Gets or sets where the column starts (character position in the layout text).</summary>
    public int Start { get; set; }

    /// <summary>Gets or sets the header label above it (columns are re-measured from the header on each page), if any.</summary>
    public string? HeaderLabel { get; set; }

    /// <summary>Gets or sets what it holds.</summary>
    public PdfFormatField Field { get; set; }
}

/// <summary>
/// A PDF format the user taught the app (Maintenance → PDF Import Formats, or from Parse PDF into
/// Bulk Add when a PDF can't be read): how to recognize the PDF and where each column of an item
/// line is. Stored as JSON in App Setting <see cref="CustomPdfFormats.SettingKey"/>.
/// </summary>
public sealed class CustomPdfFormat
{
    /// <summary>Gets or sets the name ("Acme hardware list").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets text that every PDF of this format contains (case and spacing ignored), one entry per line of the box.</summary>
    public List<string> RecognizeBy { get; set; } = new();

    /// <summary>Gets or sets the column header line (null = no header; columns are fixed positions).</summary>
    public string? HeaderLine { get; set; }

    /// <summary>Gets or sets the columns.</summary>
    public List<PdfFormatColumn> Columns { get; set; } = new();

    /// <summary>Gets or sets the text heading lines start with ("Heading #", "Set:"), null = no headings.</summary>
    public string? HeadingPrefix { get; set; }

    /// <summary>Gets or sets whether a blank manufacturer means the same as the line above.</summary>
    public bool ManufacturerCarriesDown { get; set; } = true;

    /// <summary>Gets or sets whether the manufacturer's name starts the catalog text ("McKinney TA2714 …").</summary>
    public bool ManufacturerInCatalog { get; set; }

    /// <summary>Gets or sets whether a line with only a description continues the item above.</summary>
    public bool JoinWrappedDescriptions { get; set; } = true;

    /// <summary>Gets or sets whether item lines must have a quantity (when there is a quantity column).</summary>
    public bool QuantityRequired { get; set; } = true;

    /// <summary>Gets or sets lines to skip that start with these words ("Prep:", "Note").</summary>
    public List<string> SkipLinesStartingWith { get; set; } = new();

    /// <summary>Gets or sets who taught it.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Gets or sets when it was taught or last changed (UTC).</summary>
    public DateTime UpdatedAt { get; set; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>Reads and stores taught PDF formats, and parses PDFs with them.</summary>
public static class CustomPdfFormats
{
    /// <summary>The <c>AppSetting</c> key the formats are stored under.</summary>
    public const string SettingKey = "PdfImportCustomFormats";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly Regex ColumnRegex = new(@"\S+(?: \S+)*", RegexOptions.Compiled);
    private static readonly Regex QuantityRegex = new(@"^\(?(\d{1,5}(?:,\d{3})*)(?:\.0+)?\)?(?:\s*(?:EA|EACH|PR|PAIR|SET|SETS|PCS?)\.?)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PageFooterRegex = new(@"\bPage\s+\d+\s+of\s+\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpacesRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Loads the taught formats (empty when none or unreadable).</summary>
    /// <param name="ctx">An open context.</param>
    public static List<CustomPdfFormat> Load(AppDbContext ctx)
    {
        var raw = new AppSettingRepository(ctx).GetValue(SettingKey);
        if (string.IsNullOrWhiteSpace(raw)) return new List<CustomPdfFormat>();
        try
        {
            return JsonSerializer.Deserialize<List<CustomPdfFormat>>(raw, Json) ?? new List<CustomPdfFormat>();
        }
        catch (JsonException)
        {
            return new List<CustomPdfFormat>();
        }
    }

    /// <summary>Saves the taught formats.</summary>
    /// <param name="ctx">An open context.</param>
    /// <param name="formats">All formats.</param>
    public static void Save(AppDbContext ctx, IEnumerable<CustomPdfFormat> formats) =>
        new AppSettingRepository(ctx).SetValue(SettingKey, JsonSerializer.Serialize(formats.ToList(), Json));

    /// <summary>Adds or replaces (same name, case ignored) a format.</summary>
    /// <param name="ctx">An open context.</param>
    /// <param name="format">The format.</param>
    public static void Upsert(AppDbContext ctx, CustomPdfFormat format)
    {
        var all = Load(ctx);
        all.RemoveAll(f => string.Equals(f.Name, format.Name, StringComparison.OrdinalIgnoreCase));
        format.UpdatedAt = DateTime.UtcNow;
        all.Add(format);
        Save(ctx, all.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Removes a format.</summary>
    /// <param name="ctx">An open context.</param>
    /// <param name="name">Its name.</param>
    public static void Remove(AppDbContext ctx, string name)
    {
        var all = Load(ctx);
        all.RemoveAll(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        Save(ctx, all);
    }

    /// <summary>Text squashed for comparing: upper case, no spaces.</summary>
    private static string Squash(string s) => SpacesRegex.Replace(s, string.Empty).ToUpperInvariant();

    /// <summary>Whether a PDF looks like this format (every "recognize by" text appears; a header line, if set, too).</summary>
    /// <param name="format">The format.</param>
    /// <param name="pages">Layout lines per page.</param>
    public static bool Matches(CustomPdfFormat format, IReadOnlyList<IReadOnlyList<string>> pages)
    {
        var needles = format.RecognizeBy.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Squash).ToList();
        if (!string.IsNullOrWhiteSpace(format.HeaderLine)) needles.Add(Squash(format.HeaderLine));
        if (needles.Count == 0) return false;
        var text = Squash(string.Join("\n", pages.SelectMany(p => p)));
        return needles.All(text.Contains);
    }

    /// <summary>The first taught format the PDF matches, parsed; null when none match or nothing was found.</summary>
    /// <param name="formats">The taught formats.</param>
    /// <param name="pages">Layout lines per page.</param>
    public static HardwarePdfParseResult? TryParse(IEnumerable<CustomPdfFormat> formats, IReadOnlyList<IReadOnlyList<string>> pages)
    {
        foreach (var f in formats)
        {
            if (!Matches(f, pages)) continue;
            var r = Parse(f, pages);
            if (r.Lines.Count > 0) return r;
        }
        return null;
    }

    /// <summary>Where the header's labels are on a header line (null when the line isn't the header).</summary>
    private static int[]? HeaderStarts(CustomPdfFormat format, string line)
    {
        if (format.HeaderLine == null || Squash(line) != Squash(format.HeaderLine)) return null;
        var starts = new int[format.Columns.Count];
        int from = 0;
        for (int i = 0; i < format.Columns.Count; i++)
        {
            var label = format.Columns[i].HeaderLabel;
            if (string.IsNullOrWhiteSpace(label)) { starts[i] = format.Columns[i].Start; continue; }
            int at = line.IndexOf(label.Trim(), from, StringComparison.OrdinalIgnoreCase);
            starts[i] = at >= 0 ? at : format.Columns[i].Start;
            if (at >= 0) from = at + label.Trim().Length;
        }
        return starts;
    }

    /// <summary>
    /// Splits a layout line into the format's columns: each run of text (words separated by single
    /// spaces) goes to the last column starting at or before it (3 characters' slack).
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="starts">Where each column starts.</param>
    public static string?[] Split(string line, IReadOnlyList<int> starts)
    {
        var slots = new string?[starts.Count];
        if (starts.Count == 0) return slots;
        var order = Enumerable.Range(0, starts.Count).OrderBy(i => starts[i]).ToList();
        foreach (Match chunk in ColumnRegex.Matches(line))
        {
            int which = order[0];
            foreach (var i in order)
                if (chunk.Index + 3 >= starts[i]) which = i;
            slots[which] = slots[which] == null ? chunk.Value : $"{slots[which]} {chunk.Value}";
        }
        return slots;
    }

    /// <summary>Parses a PDF with a taught format.</summary>
    /// <param name="format">The format.</param>
    /// <param name="pages">Layout lines per page.</param>
    public static HardwarePdfParseResult Parse(CustomPdfFormat format, IReadOnlyList<IReadOnlyList<string>> pages)
    {
        var result = new HardwarePdfParseResult();
        HardwarePdfParser.ReadCodeLists(pages, result);
        result.Formats.Add($"Taught format: {format.Name}");

        int Col(PdfFormatField f) => format.Columns.FindIndex(c => c.Field == f);
        int qtyCol = Col(PdfFormatField.Quantity), mfrCol = Col(PdfFormatField.Manufacturer), catCol = Col(PdfFormatField.Catalog),
            finCol = Col(PdfFormatField.Finish), descCol = Col(PdfFormatField.Description), handCol = Col(PdfFormatField.Handing);
        if (catCol < 0) return result;

        var fixedStarts = format.Columns.Select(c => c.Start).ToArray();
        string manufacturer = string.Empty;
        string? heading = null;
        ParsedHardwareLine? last = null;
        for (int p = 0; p < pages.Count; p++)
        {
            int[]? starts = format.HeaderLine == null ? fixedStarts : null;
            foreach (var raw in pages[p])
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var headerStarts = HeaderStarts(format, raw);
                if (headerStarts != null) { starts = headerStarts; continue; }
                var trimmed = raw.Trim();
                if (!string.IsNullOrWhiteSpace(format.HeadingPrefix) && trimmed.StartsWith(format.HeadingPrefix.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    heading = ImportText.Tidy(trimmed[format.HeadingPrefix.Trim().Length..]).Trim(' ', ':', '-', '(', ')');
                    if (heading.Length == 0) heading = trimmed;
                    last = null;
                    continue;
                }
                if (starts == null || PageFooterRegex.IsMatch(raw)) continue;
                if (format.SkipLinesStartingWith.Any(s => !string.IsNullOrWhiteSpace(s) && trimmed.StartsWith(s.Trim(), StringComparison.OrdinalIgnoreCase))) continue;

                var slots = Split(raw.Replace('­', '-'), starts);
                string? Get(int i) => i >= 0 ? slots[i]?.Trim() : null;
                var catalog = Get(catCol);
                var qtyText = Get(qtyCol);
                int qty = 1;
                bool qtyOk = qtyCol < 0 || (qtyText != null && QuantityRegex.IsMatch(qtyText) && int.TryParse(QuantityRegex.Match(qtyText).Groups[1].Value.Replace(",", ""), out qty));
                if (qtyCol >= 0 && qtyText == null && !format.QuantityRequired) { qtyOk = true; qty = 1; }

                if (catalog == null || !qtyOk)
                {
                    // A description wrapped onto a line of its own.
                    var desc = Get(descCol);
                    if (format.JoinWrappedDescriptions && last != null && desc != null && catalog == null && qtyText == null && Get(mfrCol) == null)
                        last.GenericName = ImportText.Tidy($"{last.GenericName} {desc}");
                    continue;
                }

                var mfrText = Get(mfrCol);
                if (mfrText != null) manufacturer = mfrText;
                else if (!format.ManufacturerCarriesDown) manufacturer = string.Empty;

                var line = new ParsedHardwareLine
                {
                    Quantity = Math.Max(1, qty),
                    GenericName = ImportText.Tidy(Get(descCol) ?? string.Empty),
                    Finish = Get(finCol),
                    SetName = heading,
                    Page = p + 1,
                };
                if (format.ManufacturerInCatalog && mfrCol < 0)
                {
                    var finish = line.Finish;
                    HardwarePdfParser.SplitMachiningText(line, catalog);
                    if (finish != null) line.Finish = finish;
                }
                else
                {
                    line.ManufacturerCode = manufacturer.Length <= 50 ? manufacturer.ToUpperInvariant() : manufacturer[..50].ToUpperInvariant();
                    line.ManufacturerName = manufacturer.Any(char.IsLower) || manufacturer.Contains(' ') ? manufacturer : null;
                    var words = ImportText.Tidy(catalog).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    for (int i = words.Count - 1; i >= 1; i--)
                        if (ImportText.Handings.Contains(words[i])) { line.Handing ??= words[i].ToUpperInvariant(); words.RemoveAt(i); }
                    if (line.Finish == null && finCol < 0 && words.Count > 1 && ImportText.IsFinish(words[^1], result.FinishCodes))
                    {
                        line.Finish = words[^1];
                        words.RemoveAt(words.Count - 1);
                    }
                    line.Catalog = string.Join(" ", words);
                }
                if (Get(handCol) is { } hand) line.Handing = hand.ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(line.Catalog)) continue;
                result.Lines.Add(line);
                last = line;
            }
        }
        return result;
    }

    /// <summary>
    /// A first guess at the columns from an example item line (and the header line, if any): the
    /// header's labels name the columns ("Qty", "Mfr", "Description", "Product Number", "Finish");
    /// otherwise a number is the quantity, a short code the manufacturer, the run with digits the
    /// catalog, a finish code the finish and plain words the description.
    /// </summary>
    /// <param name="exampleLine">An item line.</param>
    /// <param name="headerLine">The column header line, if any.</param>
    public static List<PdfFormatColumn> GuessColumns(string exampleLine, string? headerLine)
    {
        var cols = new List<PdfFormatColumn>();
        if (!string.IsNullOrWhiteSpace(headerLine))
        {
            foreach (Match m in ColumnRegex.Matches(headerLine))
                cols.Add(new PdfFormatColumn { Start = m.Index, HeaderLabel = m.Value, Field = FieldFromLabel(m.Value) });
            if (cols.Count > 0 && cols.All(c => c.Field == PdfFormatField.Ignore) == false)
                return cols;
            cols.Clear();
        }

        var chunks = ColumnRegex.Matches(exampleLine).Cast<Match>().ToList();
        bool haveCatalog = false;
        int catalogIndex = chunks.Select((c, i) => (c, i))
            .Where(x => x.c.Value.Any(char.IsDigit) && !QuantityRegex.IsMatch(x.c.Value))
            .Select(x => x.i).DefaultIfEmpty(-1).First();
        for (int i = 0; i < chunks.Count; i++)
        {
            var v = chunks[i].Value;
            PdfFormatField field;
            if (QuantityRegex.IsMatch(v) && cols.All(c => c.Field != PdfFormatField.Quantity) && i < catalogIndex) field = PdfFormatField.Quantity;
            else if (i == catalogIndex) { field = PdfFormatField.Catalog; haveCatalog = true; }
            else if (haveCatalog && ImportText.IsFinish(v, new HashSet<string>())) field = PdfFormatField.Finish;
            else if ((Regex.IsMatch(v, @"^[A-Z][A-Z0-9&]{1,5}$") || (!haveCatalog && !v.Contains(' ') && Regex.IsMatch(v, @"^[A-Z][A-Za-z&.\-]{1,19}$")))
                     && cols.All(c => c.Field != PdfFormatField.Manufacturer)) field = PdfFormatField.Manufacturer;
            else if (cols.All(c => c.Field != PdfFormatField.Description) && v.Any(char.IsLetter)) field = PdfFormatField.Description;
            else field = PdfFormatField.Ignore;
            cols.Add(new PdfFormatColumn { Start = chunks[i].Index, Field = field });
        }
        return cols;
    }

    /// <summary>
    /// A suggestion for "recognize PDFs that contain": the first line (with letters, not a page
    /// footer) that appears on every page, e.g. a report title; else the first line of page 1.
    /// </summary>
    /// <param name="pages">Layout lines per page.</param>
    public static string SuggestRecognizeText(IReadOnlyList<IReadOnlyList<string>> pages)
    {
        if (pages.Count == 0) return string.Empty;
        bool Usable(string l) => l.Trim().Length >= 6 && l.Any(char.IsLetter) && !PageFooterRegex.IsMatch(l);
        var first = pages[0].Where(Usable).Select(l => ImportText.Tidy(l)).ToList();
        if (pages.Count > 1)
        {
            var others = pages.Skip(1).Select(p => p.Select(l => Squash(l)).ToHashSet()).ToList();
            // Repeated lines can hold more than one column of text (title + job name): keep the first run.
            foreach (var l in pages[0].Where(Usable))
            {
                var run = ColumnRegex.Match(l).Value;
                if (run.Length >= 6 && run.Any(char.IsLetter) && others.All(o => o.Any(x => x.Contains(Squash(run)))))
                    return run;
            }
        }
        return first.Count > 0 ? ColumnRegex.Match(first[0]).Value : string.Empty;
    }

    /// <summary>A plain description of a format for the formats page.</summary>
    /// <param name="f">The format.</param>
    public static string Describe(CustomPdfFormat f)
    {
        var lines = new List<string> { f.Name, string.Empty };
        if (f.RecognizeBy.Count > 0) lines.Add("Used for PDFs containing: " + string.Join("  +  ", f.RecognizeBy.Select(r => $"\"{r}\"")));
        lines.Add(f.HeaderLine != null ? $"Column header: {f.HeaderLine.Trim()}" : "No column header (columns by position)");
        lines.Add("Columns: " + string.Join(", ", f.Columns.Where(c => c.Field != PdfFormatField.Ignore)
            .Select(c => $"{FieldChoices.First(x => x.Field == c.Field).Name}{(c.HeaderLabel != null ? $" (\"{c.HeaderLabel}\")" : $" (col {c.Start})")}")));
        if (f.HeadingPrefix != null) lines.Add($"Headings start with: {f.HeadingPrefix}");
        if (f.SkipLinesStartingWith.Count > 0) lines.Add("Skips lines starting with: " + string.Join(", ", f.SkipLinesStartingWith));
        var opts = new List<string>();
        if (f.ManufacturerCarriesDown) opts.Add("blank manufacturer = line above");
        if (f.ManufacturerInCatalog) opts.Add("manufacturer name starts the model text");
        if (f.JoinWrappedDescriptions) opts.Add("wrapped descriptions joined");
        opts.Add(f.QuantityRequired ? "quantity required" : "no quantity = 1");
        lines.Add("Options: " + string.Join("; ", opts));
        lines.Add(string.Empty);
        lines.Add($"Taught by {f.CreatedBy ?? "?"}, last changed {f.UpdatedAt.ToLocalTime():g}");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>One parsed line as text for a preview list.</summary>
    /// <param name="l">The line.</param>
    public static string PreviewText(ParsedHardwareLine l)
    {
        var mfr = !string.IsNullOrEmpty(l.ManufacturerName) ? l.ManufacturerName : l.ManufacturerCode;
        var parts = new List<string> { $"p{l.Page,-3}", $"{l.Quantity,4} x", $"{mfr,-14}", l.Catalog };
        if (!string.IsNullOrEmpty(l.Finish)) parts.Add($"[{l.Finish}]");
        if (!string.IsNullOrEmpty(l.Handing)) parts.Add($"({l.Handing})");
        if (!string.IsNullOrEmpty(l.GenericName)) parts.Add($"- {l.GenericName}");
        if (!string.IsNullOrEmpty(l.SetName)) parts.Add($"  <{l.SetName}>");
        return string.Join("  ", parts);
    }

    /// <summary>The choices for a column's field, with display names.</summary>
    public static IReadOnlyList<(PdfFormatField Field, string Name)> FieldChoices { get; } = new[]
    {
        (PdfFormatField.Ignore, "(not used)"),
        (PdfFormatField.Quantity, "Quantity"),
        (PdfFormatField.Manufacturer, "Manufacturer"),
        (PdfFormatField.Catalog, "Model / catalog number"),
        (PdfFormatField.Finish, "Finish"),
        (PdfFormatField.Description, "Description"),
        (PdfFormatField.Handing, "Handing"),
    };

    /// <summary>The field a header label stands for.</summary>
    /// <param name="label">The label.</param>
    public static PdfFormatField FieldFromLabel(string label)
    {
        var l = Squash(label).TrimEnd('.', ':', '#');
        if (l is "QTY" or "QUANTITY" or "QTYEA" or "EA" or "COUNT") return PdfFormatField.Quantity;
        if (l is "MFG" or "MFR" or "MFGR" or "MANUF" or "MANUFACTURER" or "MAKE" or "VENDOR" or "BRAND") return PdfFormatField.Manufacturer;
        if (l is "FINISH" or "FIN" or "FNSH") return PdfFormatField.Finish;
        if (l is "HAND" or "HANDING" or "HND") return PdfFormatField.Handing;
        if (l.Contains("DESCRIPTION") || l is "DESC" or "ITEM" or "NAME" or "TYPE") return PdfFormatField.Description;
        if (l.Contains("PRODUCT") || l.Contains("CATALOG") || l.Contains("MODEL") || l.Contains("PART") || l is "ITEM#" or "CAT")
            return PdfFormatField.Catalog;
        return PdfFormatField.Ignore;
    }
}
