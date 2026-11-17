using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>What <see cref="HardwarePdfParser.Parse"/> found in a PDF.</summary>
public sealed class HardwarePdfParseResult
{
    /// <summary>Gets the hardware lines, in document order (not yet de-duplicated).</summary>
    public List<ParsedHardwareLine> Lines { get; } = new();

    /// <summary>Gets the PDF's own manufacturer code list (code → name), if it has one.</summary>
    public Dictionary<string, string> ManufacturerCodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the PDF's finish codes, if it has a finish list.</summary>
    public HashSet<string> FinishCodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the layouts that were recognised (e.g. "Hardware schedule", "Component costing").</summary>
    public List<string> Formats { get; } = new();
}

/// <summary>
/// Reads hardware lines out of a PDF's layout text. Understands:
/// <list type="bullet">
/// <item><b>Hardware schedules / spec hardware sets</b> (e.g. Comsense submittals): "Heading #… (Set #…)"
/// followed by item lines "H026  270 Hinges  5BB1 4 1/2 x 4 1/2 NRP  651  IV", with wrapped
/// continuation lines. Scanned schedules read with OCR use the same parser.</item>
/// <item><b>Component costing / pricing reports</b>: "Mfg  Product Description  List Price …" lines
/// such as "LC  Concealed Closer 2033 2,871.00 …" with the description wrapped below and an
/// "Openings:" line.</item>
/// </list>
/// Anything else (e.g. shop drawings) is left to <see cref="HardwareImportMatcher.FindKnownCallouts"/>,
/// which looks for callouts the database already knows.
/// </summary>
public static class HardwarePdfParser
{
    private static readonly Regex HeadingRegex = new(@"Heading\s*#\s*([\w.\-]+)\s*\(\s*Set\s*#\s*([\w.\-]+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ScheduleItemRegex = new(@"^\s{0,16}([A-Z][0-9OIl$S]{3})\s+(\d{1,3}(?:,\d{3})*|\d{1,6})[A-Za-z]?\s+(\S.*)$", RegexOptions.Compiled);
    private static readonly Regex ColumnRegex = new(@"\S+(?: \S+)*", RegexOptions.Compiled);
    private static readonly Regex ManufacturerCodeRegex = new(@"^[A-Za-z$][A-Za-z0-9$]{0,4}$", RegexOptions.Compiled);
    private static readonly Regex FooterRegex = new(@"^\s*([A-Z][a-z]{2} \d{1,2}, \d{4}|Page \d+ of \d+)", RegexOptions.Compiled);
    private static readonly Regex OpeningLineRegex = new(@"^\s*\d+\s+(Single|Pair|Double)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CostingHeaderRegex = new(@"^\s*Mfg\s+Product Description", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CostingItemRegex = new(@"^([A-Z][A-Z0-9]{1,4})\s+(\S.*?)\s+-?[\d,]+\.\d\d(\s|$)", RegexOptions.Compiled);
    private static readonly Regex ListRowRegex = new(@"^\s*(\S{1,8}(?: \S{1,4})?)\s{2,}(\S.*)$", RegexOptions.Compiled);

    /// <summary>Parses all pages.</summary>
    /// <param name="pages">Layout lines per page (from <see cref="HardwarePdfTextReader"/>).</param>
    public static HardwarePdfParseResult Parse(IReadOnlyList<IReadOnlyList<string>> pages)
    {
        var result = new HardwarePdfParseResult();
        ReadCodeLists(pages, result);

        bool schedule = false, costing = false;
        string? heading = null;
        for (int p = 0; p < pages.Count; p++)
        {
            var lines = pages[p];
            if (lines.Any(l => CostingHeaderRegex.IsMatch(l))) costing = true;
            if (costing && lines.Any(l => CostingHeaderRegex.IsMatch(l)))
                ParseCostingPage(lines, p + 1, result);
            else
                schedule |= ParseSchedulePage(lines, p + 1, result, ref heading);
        }
        if (schedule) result.Formats.Add("Hardware schedule");
        if (costing) result.Formats.Add("Component costing");

        foreach (var line in result.Lines)
        {
            if (string.IsNullOrEmpty(line.ManufacturerName) &&
                result.ManufacturerCodes.TryGetValue(line.ManufacturerCode, out var name))
                line.ManufacturerName = name;
        }
        return result;
    }

    // ── Manufacturer / finish lists ─────────────────────────────────────────

    private static void ReadCodeLists(IReadOnlyList<IReadOnlyList<string>> pages, HardwarePdfParseResult result)
    {
        foreach (var lines in pages)
        {
            string? mode = null;
            foreach (var raw in lines)
            {
                var t = raw.Trim();
                if (t.StartsWith("Manufacturer List", StringComparison.OrdinalIgnoreCase)) { mode = "m"; continue; }
                if (t.StartsWith("Finish List", StringComparison.OrdinalIgnoreCase)) { mode = "f"; continue; }
                if (Regex.IsMatch(t, @"^(Option|Opening|Product|Keying)\s+List|^Hardware Schedule|^Heading", RegexOptions.IgnoreCase)) { mode = null; continue; }
                if (mode == null || t.Length == 0 || FooterRegex.IsMatch(raw)) continue;
                if (Regex.IsMatch(t, @"^Code\s{2,}", RegexOptions.IgnoreCase)) continue;
                var m = ListRowRegex.Match(raw);
                if (!m.Success) continue;
                var code = m.Groups[1].Value.Trim();
                if (mode == "m") result.ManufacturerCodes[ImportText.CleanManufacturerCode(code)] = m.Groups[2].Value.Trim();
                else result.FinishCodes.Add(code);
            }
        }
    }

    // ── Hardware schedule / spec sets ───────────────────────────────────────

    private static bool ParseSchedulePage(IReadOnlyList<string> lines, int page, HardwarePdfParseResult result, ref string? heading)
    {
        bool found = false;
        ParsedHardwareLine? current = null;
        int nameCol = 0, catalogCol = -1;

        foreach (var line in lines)
        {
            var h = HeadingRegex.Match(line);
            if (h.Success) { heading = h.Groups[2].Value; current = null; continue; }

            var m = ScheduleItemRegex.Match(line);
            if (m.Success)
            {
                int restCol = m.Groups[3].Index;
                var parts = ColumnRegex.Matches(m.Groups[3].Value)
                    .Select(c => (Col: c.Index + restCol, Text: c.Value))
                    .ToList();
                if (parts.Count < 2) { current = null; continue; }

                bool hasMfr = ManufacturerCodeRegex.IsMatch(parts[^1].Text);
                var middle = parts.Skip(1).Take(parts.Count - (hasMfr ? 2 : 1)).ToList();

                current = new ParsedHardwareLine
                {
                    ManufacturerCode = hasMfr ? ImportText.CleanManufacturerCode(parts[^1].Text) : string.Empty,
                    GenericName = parts[0].Text,
                    Quantity = int.TryParse(m.Groups[2].Value.Replace(",", ""), out var q) ? q : 0,
                    SetName = heading,
                    Page = page,
                };
                nameCol = parts[0].Col;
                catalogCol = middle.Count > 0 ? middle[0].Col : -1;

                var catalog = new List<string>();
                for (int i = 0; i < middle.Count; i++)
                {
                    var text = middle[i].Text;
                    if (i > 0 && ImportText.Handings.Contains(text)) current.Handing = text;
                    else if (i > 0 && ImportText.IsFinish(text, result.FinishCodes)) current.Finish = text;
                    else catalog.Add(text);
                }
                current.Catalog = string.Join(" ", catalog);

                // Long item names run into the catalog with a single space ("Door Contact (By Others) 8080-T-W").
                if (current.Catalog.Length == 0) SplitNameAndCatalog(current);

                result.Lines.Add(current);
                found = true;
                continue;
            }

            if (current == null) continue;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || FooterRegex.IsMatch(line) || OpeningLineRegex.IsMatch(line) ||
                trimmed.StartsWith("Note", StringComparison.OrdinalIgnoreCase) || catalogCol < 0)
            {
                current = null;
                continue;
            }

            // Continuation line: text under the catalog column extends the catalog; text under the
            // name column extends the name (and anything further right, the catalog).
            int indent = line.Length - line.TrimStart().Length;
            var segments = ColumnRegex.Matches(line).Select(c => (Col: c.Index, Text: c.Value)).ToList();
            if (indent >= catalogCol - 2)
            {
                AppendCatalog(current, segments.Select(s => s.Text), result.FinishCodes);
            }
            else if (indent >= nameCol - 2)
            {
                current.GenericName = $"{current.GenericName} {segments[0].Text}";
                AppendCatalog(current, segments.Skip(1).Select(s => s.Text), result.FinishCodes);
            }
            else
            {
                current = null;
            }
        }
        return found;
    }

    private static void AppendCatalog(ParsedHardwareLine line, IEnumerable<string> segments, ISet<string> finishes)
    {
        foreach (var s in segments)
        {
            if (ImportText.Handings.Contains(s)) { line.Handing ??= s; continue; }
            if (ImportText.IsFinish(s, finishes) && s.Length <= 6) { line.Finish ??= s; continue; }
            line.Catalog = line.Catalog.Length == 0 ? s : $"{line.Catalog} {s}";
        }
    }

    private static void SplitNameAndCatalog(ParsedHardwareLine line)
    {
        var words = line.GenericName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int i = Array.FindIndex(words, w => w.Any(char.IsDigit));
        if (i <= 0) return;
        line.Catalog = string.Join(" ", words.Skip(i));
        line.GenericName = string.Join(" ", words.Take(i));
    }

    // ── Component costing / pricing reports ─────────────────────────────────

    private static void ParseCostingPage(IReadOnlyList<string> lines, int page, HardwarePdfParseResult result)
    {
        ParsedHardwareLine? current = null;
        string text = string.Empty;

        void Finish()
        {
            if (current == null) return;
            SplitCostingText(current, text, result.FinishCodes);
            result.Lines.Add(current);
            current = null;
        }

        foreach (var line in lines)
        {
            var m = CostingItemRegex.Match(line);
            if (m.Success)
            {
                Finish();
                current = new ParsedHardwareLine { ManufacturerCode = ImportText.CleanManufacturerCode(m.Groups[1].Value), Page = page };
                text = m.Groups[2].Value.Trim();
                var qty = Regex.Match(line, @"\s(\d{1,5})\s+[\d,]+\.\d\d\s*$");
                if (qty.Success) current.Quantity = int.Parse(qty.Groups[1].Value);
                continue;
            }
            if (current == null) continue;

            var t = line.Trim();
            if (t.StartsWith("Openings:", StringComparison.OrdinalIgnoreCase))
            {
                current.SetName = t.Substring("Openings:".Length).Trim();
                Finish();
            }
            else if (line.StartsWith("    ") && t.Length > 0 && !Regex.IsMatch(t, "Subtotal|Cost Group", RegexOptions.IgnoreCase))
            {
                text = $"{text} {t}";
            }
            else
            {
                Finish();
            }
        }
        Finish();
    }

    /// <summary>
    /// Splits "Concealed Closer 2033 BUMP 639" into name "Concealed Closer" and catalog "2033 BUMP 639":
    /// the catalog starts at the first word with a digit or the first all-caps code after the name.
    /// Trailing finish codes and a repeat of the manufacturer code are removed.
    /// </summary>
    private static void SplitCostingText(ParsedHardwareLine line, string text, ISet<string> finishes)
    {
        var words = ImportText.Tidy(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int i = 0;
        while (i < words.Count && !words[i].Any(char.IsDigit) &&
               !(i > 0 && Regex.IsMatch(words[i], @"^[A-Z]{2,}[A-Z0-9./\-]*$")))
            i++;
        line.GenericName = string.Join(" ", words.Take(i));
        var catalog = words.Skip(i).ToList();
        while (catalog.Count > 1 &&
               (string.Equals(catalog[^1], line.ManufacturerCode, StringComparison.OrdinalIgnoreCase) ||
                ImportText.IsFinish(catalog[^1], finishes) ||
                catalog[^1] is "CUS" or "BRONZE"))
        {
            if (line.Finish == null && ImportText.IsFinish(catalog[^1], finishes)) line.Finish = catalog[^1];
            catalog.RemoveAt(catalog.Count - 1);
        }
        line.Catalog = string.Join(" ", catalog);
    }
}
