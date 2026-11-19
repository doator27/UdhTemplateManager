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
/// <item><b>Door machining sheets</b> (one door type per page): a "Qty  Hardware Details" section in
/// the left column with item lines "3  Ives BAA 5BB1 4 1/2" x 4 1/2" 630 NRP" (manufacturer name
/// first) and "Prep: …" notes below, and "1 Opening (LHR): E100C" lines that say how many doors use
/// the page.</item>
/// Anything else (e.g. shop drawings) is left to <see cref="HardwareImportMatcher.FindKnownCallouts"/>,
/// which looks for callouts the database already knows.
/// </summary>
public static class HardwarePdfParser
{
    private static readonly Regex HeadingRegex = new(@"Heading\s*#\s*([\w.\-]+)\s*\(\s*(?:Set\s*#|Hardware\s+Set|Group\s*:|Set\s*:)\s*([^)]*?)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QtyFirstItemRegex = new(@"^\s{0,30}(\d{1,5}(?:,\d{3})*)\s{2,}(\S.*)$", RegexOptions.Compiled);
    private static readonly Regex OpeningAnywhereRegex = new(@"\b\d+\s+(Single|Pair|Double)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NameNumberPrefixRegex = new(@"^\d[\w.]*\s+-\s+", RegexOptions.Compiled);
    private static readonly Regex TitleWordRegex = new(@"^[A-Z][a-z]+$", RegexOptions.Compiled);

    /// <summary>Second words that belong to a manufacturer's name ("dormakaba Architectural", "Norton Rixson").</summary>
    private static readonly HashSet<string> ManufacturerSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Architectural", "Duprin", "Rixson", "Others", "Hardware", "Products", "Industries", "Closers", "Locks",
        "Manufacturing", "Group", "Systems", "Security", "Johnson", "Shop", "Companies", "Company", "Inc",
    };
    private static readonly Regex ScheduleItemRegex = new(@"^\s{0,16}([A-Z][0-9OIl$S]{3})\s+(\d{1,3}(?:,\d{3})*|\d{1,6})[A-Za-z]?\s+(\S.*)$", RegexOptions.Compiled);
    private static readonly Regex ColumnRegex = new(@"\S+(?: \S+)*", RegexOptions.Compiled);
    private static readonly Regex ManufacturerCodeRegex = new(@"^[A-Za-z$][A-Za-z0-9$]{0,4}$", RegexOptions.Compiled);
    private static readonly Regex FooterRegex = new(@"^\s*([A-Z][a-z]{2} \d{1,2}, \d{4}|Page \d+ of \d+)", RegexOptions.Compiled);
    private static readonly Regex OpeningLineRegex = new(@"^\s*\d+\s+(Single|Pair|Double)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CostingHeaderRegex = new(@"^\s*Mfg\s+Product Description", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CostingItemRegex = new(@"^(\s*)([A-Z][A-Z0-9]{1,4})\s+(\S.*?)\s+-?[\d,]+\.\d\d(\s|$)", RegexOptions.Compiled);
    private static readonly Regex MachiningHeaderRegex = new(@"^(\s*)Qty\s+(Hardware Details)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MachiningItemRegex = new(@"^(\s*)(\d{1,3})(\s+)(\S.*)$", RegexOptions.Compiled);
    private static readonly Regex MachiningOpeningRegex = new(@"(\d{1,4})\s+Openings?\s*(?:\(([A-Z]{1,4})\))?\s*:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MachiningTypeRegex = new(@"\bType:\s*(\S.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex ColumnGapRegex = new(@"\s{6,}", RegexOptions.Compiled);
    private static readonly Regex DoorThicknessRegex = new(@"\s+\d\s+\d/\d$", RegexOptions.Compiled);

    /// <summary>Compliance codes printed with a model ("Ives BAA 5BB1"); moved to the end of the callout.</summary>
    private static readonly HashSet<string> ComplianceCodes = new(StringComparer.OrdinalIgnoreCase) { "BAA", "TAA", "ARRA" };

    private static readonly Regex ListRowRegex = new(@"^\s*(\S{1,8}(?: \S{1,4})?)\s{2,}(\S.*)$", RegexOptions.Compiled);

    /// <summary>Parses all pages.</summary>
    /// <param name="pages">Layout lines per page (from <see cref="HardwarePdfTextReader"/>).</param>
    public static HardwarePdfParseResult Parse(IReadOnlyList<IReadOnlyList<string>> pages)
    {
        var result = new HardwarePdfParseResult();
        ReadCodeLists(pages, result);

        bool schedule = false, costing = false, machining = false;
        string? heading = null;
        for (int p = 0; p < pages.Count; p++)
        {
            var lines = pages[p];
            if (lines.Any(l => MachiningHeaderRegex.IsMatch(l)))
            {
                machining |= ParseMachiningPage(lines, p + 1, result);
                continue;
            }
            if (lines.Any(l => CostingHeaderRegex.IsMatch(l))) costing = true;
            if (costing && lines.Any(l => CostingHeaderRegex.IsMatch(l)))
                ParseCostingPage(lines, p + 1, result);
            else
                schedule |= ParseSchedulePage(lines, p + 1, result, ref heading);
        }
        if (schedule) result.Formats.Add("Hardware schedule");
        if (costing)
        {
            result.Formats.Add("Component costing");
            MatchCostSummaryNames(pages, result);
        }
        if (machining) result.Formats.Add("Door machining sheet");
        if (result.Lines.Count == 0 && ParseQtyFirstSchedule(pages, result))
            result.Formats.Add("Hardware schedule (quantity first)");

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

    // ── Quantity-first hardware schedules ───────────────────────────────────

    private sealed record QtyFirstRow(int Page, string? Heading, int Quantity, List<(int Col, string Text)> Cols);

    /// <summary>
    /// Reads schedules whose item lines start with the quantity and name the manufacturer in the
    /// callout: "171   Standard Hinge   BEST FBB179 (4 1/2" x 4 1/2") US26D   US26D" or
    /// "3   003.1 - Standard Hinge   McKinney MPB91 4 1/2" x 4 1/2" NRP   US32D", under
    /// "Heading #1 (Hardware Set U01 - ENTRY)" / "Heading #1 (Group: E1)". Quantities are the
    /// heading's totals. The manufacturer is the callout's first word, or two words when the
    /// second is part of the name ("Norton Rixson", "By Others"); the finish (last column) and
    /// handing are taken out of the callout. Lines under an item are notes ("**Push Side") and are
    /// skipped; door/opening lines and "Note …" lines are not items.
    /// </summary>
    private static bool ParseQtyFirstSchedule(IReadOnlyList<IReadOnlyList<string>> pages, HardwarePdfParseResult result)
    {
        var rows = new List<QtyFirstRow>();
        bool sawHeading = false;
        string? heading = null;
        for (int p = 0; p < pages.Count; p++)
        {
            foreach (var line in pages[p])
            {
                var h = HeadingRegex.Match(line);
                if (h.Success)
                {
                    heading = h.Groups[2].Value.Trim();
                    sawHeading = true;
                    continue;
                }
                var m = QtyFirstItemRegex.Match(line);
                if (!m.Success || OpeningAnywhereRegex.IsMatch(line)) continue;
                int offset = m.Groups[2].Index;
                var cols = ColumnRegex.Matches(m.Groups[2].Value).Select(c => (Col: c.Index + offset, Text: c.Value)).ToList();
                if (!int.TryParse(m.Groups[1].Value.Replace(",", ""), out var qty) || qty <= 0) continue;
                rows.Add(new QtyFirstRow(p + 1, heading, qty, cols));
            }
        }
        if (!sawHeading || rows.Count(r => r.Cols.Count >= 2) < 2) return false;

        // Where the callout column usually starts, and the manufacturer names seen there.
        int mode = rows.Where(r => r.Cols.Count >= 2).GroupBy(r => r.Cols[1].Col).OrderByDescending(g => g.Count()).First().Key;
        var mfrWords = new HashSet<string>(rows
            .Where(r => r.Cols.Count >= 2 && Math.Abs(r.Cols[1].Col - mode) <= 3)
            .Select(r => r.Cols[1].Text.Split(' ')[0]), StringComparer.Ordinal);

        var items = new List<(QtyFirstRow Row, string Name, string Callout, string? Finish)>();
        foreach (var row in rows)
        {
            var cols = row.Cols.Select(c => c.Text).ToList();
            // The name ran into the callout ("Manual Flush Bolt HMD Rockwood 555 US26D"): split at a known manufacturer.
            if (cols.Count < 2 || Math.Abs(row.Cols[1].Col - mode) > 3)
            {
                var words = cols[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int at = Array.FindLastIndex(words, w => mfrWords.Contains(w));
                if (at >= 1 && at < words.Length - 1)
                {
                    cols.RemoveAt(0);
                    cols.Insert(0, string.Join(" ", words.Skip(at)));
                    cols.Insert(0, string.Join(" ", words.Take(at)));
                }
            }
            if (cols.Count < 2) continue;
            var name = NameNumberPrefixRegex.Replace(cols[0], string.Empty).Trim();
            var callout = string.Join(" ", cols.Skip(1).Take(Math.Max(1, cols.Count - 2)));
            string? finish = cols.Count > 2 ? cols[^1] : null;
            if (callout.StartsWith("Note ", StringComparison.OrdinalIgnoreCase)) continue;
            items.Add((row, name, ImportText.Tidy(callout), finish));
        }

        var firstCounts = items.GroupBy(i => i.Callout.Split(' ')[0], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var twoCounts = items.Select(i => i.Callout.Split(' ')).Where(w => w.Length > 2)
            .GroupBy(w => $"{w[0]} {w[1]}", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        bool found = false;
        foreach (var (row, name, callout, finish) in items)
        {
            var words = callout.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            int n = 1;
            if (words.Count > 2 && TitleWordRegex.IsMatch(words[1]) &&
                (ManufacturerSuffixes.Contains(words[1]) ||
                 twoCounts.GetValueOrDefault($"{words[0]} {words[1]}") >= Math.Max(2, firstCounts.GetValueOrDefault(words[0]) / 2.0)))
                n = 2;
            var mfr = string.Join(" ", words.Take(n));
            var line = new ParsedHardwareLine
            {
                ManufacturerName = mfr,
                ManufacturerCode = Truncate(mfr.ToUpperInvariant(), 50),
                GenericName = name,
                Finish = finish,
                Quantity = row.Quantity,
                SetName = row.Heading,
                Page = row.Page,
            };

            var finishes = new HashSet<string>((finish ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            var rest = words.Skip(n).ToList();
            for (int i = rest.Count - 1; i >= 1; i--)
            {
                if (ImportText.Handings.Contains(rest[i])) { line.Handing ??= rest[i].ToUpperInvariant(); rest.RemoveAt(i); }
                else if (finishes.Contains(rest[i])) rest.RemoveAt(i);
            }
            if (line.Finish == null && rest.Count > 1 && ImportText.IsFinish(rest[^1], result.FinishCodes))
            {
                line.Finish = rest[^1];
                rest.RemoveAt(rest.Count - 1);
            }
            var catalog = DoorThicknessRegex.Replace(string.Join(" ", rest), string.Empty).Trim();
            line.Catalog = Regex.Replace(catalog, @"\s+-$", string.Empty);
            if (line.Catalog.Length == 0) continue;
            result.Lines.Add(line);
            found = true;
        }
        return found;
    }

    // ── Door machining sheets ───────────────────────────────────────────────

    /// <summary>
    /// Reads one door machining sheet page: the item lines under "Qty  Hardware Details" (left
    /// column only: text after a wide gap belongs to the drawing and is cut off), stopping at the
    /// first line that starts left of the details column (e.g. "Prep for … locations."). The
    /// page's "N Opening (hand): mark" lines give how many doors use it; quantities are
    /// per door, so they are multiplied by that count.
    /// </summary>
    private static bool ParseMachiningPage(IReadOnlyList<string> lines, int page, HardwarePdfParseResult result)
    {
        int header = -1, detailsCol = 0;
        for (int i = 0; i < lines.Count && header < 0; i++)
        {
            var m = MachiningHeaderRegex.Match(lines[i]);
            if (!m.Success) continue;
            header = i;
            detailsCol = m.Groups[2].Index;
        }
        if (header < 0) return false;

        // Openings and door type (anywhere on the page).
        int openings = 0;
        var marks = new List<string>();
        string? type = null;
        foreach (var line in lines)
        {
            foreach (Match o in MachiningOpeningRegex.Matches(line))
            {
                openings += int.Parse(o.Groups[1].Value);
                marks.Add(o.Groups[3].Value);
            }
            var t = MachiningTypeRegex.Match(line);
            if (t.Success && type == null) type = t.Groups[1].Value;
        }
        string? setName = marks.Count > 0
            ? (type != null ? $"Type {type}: " : string.Empty) + string.Join(", ", marks)
            : type != null ? $"Type {type}" : null;

        var items = new List<(ParsedHardwareLine Line, string Text, List<string> Notes)>();
        for (int i = header + 1; i < lines.Count; i++)
        {
            var raw = lines[i];
            if (raw.Trim().Length == 0) continue;
            int indent = raw.Length - raw.TrimStart().Length;
            var item = MachiningItemRegex.Match(raw);
            int textCol = item.Success ? item.Groups[4].Index : indent;

            if (item.Success && indent < detailsCol - 2 && Math.Abs(textCol - detailsCol) <= 3)
            {
                var text = LeftColumn(item.Groups[4].Value);
                int qty = int.Parse(item.Groups[2].Value);
                items.Add((new ParsedHardwareLine { Page = page, Quantity = qty * Math.Max(1, openings), SetName = setName }, text, new List<string>()));
            }
            else if (items.Count > 0 && Math.Abs(indent - detailsCol) <= 3)
            {
                items[^1].Notes.Add(LeftColumn(raw.Trim()));
            }
            else if (indent > detailsCol + 3)
            {
                // Drawing text (dimensions) between the items.
                continue;
            }
            else
            {
                break;
            }
        }

        foreach (var (line, text, notes) in items)
        {
            SplitMachiningText(line, text);
            var prep = notes.FirstOrDefault(n => n.StartsWith("Prep:", StringComparison.OrdinalIgnoreCase));
            if (prep != null)
                line.GenericName = Regex.Split(prep.Substring(5).Trim(), @",|\s-\s")[0].Trim();
            if (line.Catalog.Length > 0) result.Lines.Add(line);
        }
        return items.Count > 0;
    }

    /// <summary>The text before the first wide gap (what follows belongs to the drawing).</summary>
    private static string LeftColumn(string text) => ColumnGapRegex.Split(text.Trim())[0].Trim();

    /// <summary>
    /// Splits "Ives BAA 5BB1 4 1/2" x 4 1/2" 630 NRP" into manufacturer "Ives" and callout
    /// "5BB1 4 1/2" x 4 1/2" NRP BAA" (finish 630). The manufacturer is the leading capitalised
    /// words ("Von Duprin") or one all-caps word ("LCN"); compliance codes move to the end; the
    /// finish and handing words are taken out (never the model itself); a trailing door thickness
    /// ("1 3/4") is dropped.
    /// </summary>
    private static void SplitMachiningText(ParsedHardwareLine line, string text)
    {
        var words = ImportText.Tidy(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int n = 0;
        while (n < words.Count && Regex.IsMatch(words[n], @"^[A-Z][a-z][A-Za-z&'.]*$")) n++;
        if (n == 0 && words.Count > 1 && Regex.IsMatch(words[0], @"^[A-Z][A-Z&]{1,5}$") && !ComplianceCodes.Contains(words[0])) n = 1;
        var name = string.Join(" ", words.Take(n));
        line.ManufacturerName = name.Length > 0 ? name : null;
        line.ManufacturerCode = name.Length > 0 ? Truncate(name.ToUpperInvariant(), 50) : string.Empty;

        var rest = words.Skip(n).ToList();
        var compliance = new List<string>();
        while (rest.Count > 1 && ComplianceCodes.Contains(rest[0]))
        {
            compliance.Add(rest[0]);
            rest.RemoveAt(0);
        }
        for (int i = rest.Count - 1; i >= 1; i--)
        {
            var w = rest[i];
            if (line.Handing == null && ImportText.Handings.Contains(w)) { line.Handing = w.ToUpperInvariant(); rest.RemoveAt(i); }
            else if (line.Finish == null && Regex.IsMatch(w, @"^\d{3}$|^US\d{1,2}[A-Z]?$", RegexOptions.IgnoreCase)) { line.Finish = w; rest.RemoveAt(i); }
        }
        var catalog = string.Join(" ", rest.Concat(compliance));
        catalog = DoorThicknessRegex.Replace(catalog, string.Empty).Trim();
        line.Catalog = catalog;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // ── Component costing / pricing reports ─────────────────────────────────

    private static void ParseCostingPage(IReadOnlyList<string> lines, int page, HardwarePdfParseResult result)
    {
        ParsedHardwareLine? current = null;
        string text = string.Empty;
        int itemIndent = 0;

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
                current = new ParsedHardwareLine { ManufacturerCode = ImportText.CleanManufacturerCode(m.Groups[2].Value), Page = page };
                text = m.Groups[3].Value.Trim();
                itemIndent = m.Groups[1].Value.Length;
                var qty = Regex.Match(line, @"\s(\d{1,5})\s+([\d,]+\.\d\d)\s*$");
                if (qty.Success)
                {
                    current.Quantity = int.Parse(qty.Groups[1].Value);
                    current.ExtendedCost = decimal.Parse(qty.Groups[2].Value.Replace(",", string.Empty), System.Globalization.CultureInfo.InvariantCulture);
                }
                continue;
            }
            if (current == null) continue;

            var t = line.Trim();
            if (t.StartsWith("Openings:", StringComparison.OrdinalIgnoreCase))
            {
                current.SetName = t.Substring("Openings:".Length).Trim();
                Finish();
            }
            else if (t.Length > 0 && line.Length - line.TrimStart().Length >= itemIndent + 3 &&
                     !Regex.IsMatch(t, "Subtotal|Cost Group", RegexOptions.IgnoreCase))
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

    private static readonly Regex SummaryTotalRegex = new(@"^\s*(\S.*?)\s+Total:\s+.*?([\d,]+\.\d\d)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Works out the manufacturer names behind a costing report's codes ("AC01", "S701") from its
    /// "Project Cost Summary": each manufacturer's net total there equals the sum of its code's
    /// extended costs ("Accurate Lock Total: … 27,944.00" = every AC01 line). Only totals that
    /// match exactly one name and one code are used; codes the PDF already names are kept.
    /// </summary>
    private static void MatchCostSummaryNames(IReadOnlyList<IReadOnlyList<string>> pages, HardwarePdfParseResult result)
    {
        var names = new List<(string Name, decimal Total)>();
        foreach (var lines in pages)
        {
            if (!lines.Any(l => l.Contains("Cost Summary", StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var line in lines)
            {
                var m = SummaryTotalRegex.Match(line);
                if (!m.Success) continue;
                var name = m.Groups[1].Value.Trim();
                if (name.Length < 2 || !name.Any(char.IsLetter)) continue;
                names.Add((name, decimal.Parse(m.Groups[2].Value.Replace(",", string.Empty), System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        if (names.Count == 0) return;

        var codeTotals = result.Lines
            .Where(l => l.ManufacturerCode.Length > 0 && l.ExtendedCost != null)
            .GroupBy(l => l.ManufacturerCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Code: g.Key, Total: g.Sum(l => l.ExtendedCost!.Value)))
            .ToList();
        foreach (var (code, total) in codeTotals)
        {
            if (result.ManufacturerCodes.ContainsKey(code)) continue;
            var byName = names.Where(n => Math.Abs(n.Total - total) < 0.02m).ToList();
            var byCode = codeTotals.Count(c => Math.Abs(c.Total - total) < 0.02m);
            if (byName.Count == 1 && byCode == 1)
                result.ManufacturerCodes[code] = Regex.Replace(byName[0].Name, @"\s+-\s+'.*'$", string.Empty);
        }
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
