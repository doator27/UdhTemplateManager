using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HardwareTemplateBuilder.Core.Models;
using HardwareTemplateBuilder.Core.Services.Import;

namespace HardwareTemplateBuilder.Core.Services.Catalogs;

/// <summary>
/// Reads one product catalog page for a callout: how well the page matches, the template
/// numbers it lists, what the callout's option codes mean, the product description, and a short
/// snippet. Pure text functions (no database) so they can be tested on page text.
/// Page text is layout text: one line per line, columns separated by two or more spaces.
/// </summary>
public static class CatalogPageAnalyzer
{
    private static readonly Regex TemplateLabel = new(
        @"\bTEMPLATE\b\s*(?:NO\.?|NUMBER|NUM\.?|#)?\s*[:#.]?\s*(?<n>[A-Z0-9][A-Z0-9\-./]*\d[A-Z0-9\-./]*|\d[A-Z0-9\-./]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Columns = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex Words = new(@"[A-Za-z][A-Za-z']+", RegexOptions.Compiled);
    private static readonly Regex Separator = new(@"^\s*[-–—=:]\s*", RegexOptions.Compiled);

    /// <summary>Words that follow "TEMPLATE" but are not template numbers.</summary>
    private static readonly HashSet<string> NotNumbers = new(StringComparer.OrdinalIgnoreCase) { "AVAILABLE", "REQUIRED", "SEE", "PAGE", "FOR" };

    /// <summary>The callout without sizes/notes, split into whitespace tokens.</summary>
    /// <param name="callout">A callout such as "4040XP EDA RW/PA AL".</param>
    public static List<string> Tokens(string callout) =>
        ImportText.StripNotesAndSize(callout ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    /// <summary>
    /// The comparison keys to search for the product, most specific first: the first token
    /// ("4040XP"), its first hyphen part ("9875L" of "9875L-F"), and its leading letters + digits ("9875").
    /// Keys shorter than 3 characters are left out.
    /// </summary>
    /// <param name="callout">The callout.</param>
    public static List<string> ModelKeys(string callout)
    {
        var tokens = Tokens(callout);
        var result = new List<string>();
        if (tokens.Count == 0) return result;
        var first = tokens[0];
        var candidates = new List<string> { first, first.Split('-', '/')[0] };
        var lead = Regex.Match(first.ToUpperInvariant(), @"^[A-Z]{0,4}\d+");
        if (lead.Success) candidates.Add(lead.Value);
        foreach (var c in candidates)
        {
            var key = ImportText.Key(c);
            if (key.Length >= 3 && !result.Contains(key)) result.Add(key);
        }
        return result;
    }

    /// <summary>
    /// The option codes in a callout: every token after the model (and the hyphen parts after the
    /// model's first part), without finishes, handings, sizes and one-letter codes.
    /// "4040XP EDA RW/PA AL" gives EDA and RW/PA; "QEL-9875L-F 06 US26D" gives QEL and F is too short.
    /// </summary>
    /// <param name="callout">The callout.</param>
    public static List<string> OptionCodes(string callout)
    {
        var tokens = Tokens(callout);
        var parts = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var pieces = tokens[i].Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (i == 0)
            {
                // "QEL-9875L-F": the model is the piece with digits; the others are options.
                int model = Array.FindIndex(pieces, p => p.Any(char.IsDigit));
                for (int j = 0; j < pieces.Length; j++) if (j != model) parts.Add(pieces[j]);
            }
            else
            {
                parts.Add(tokens[i]);
                if (pieces.Length > 1) parts.AddRange(pieces);
            }
        }
        var result = new List<string>();
        var seen = new HashSet<string>();
        foreach (var p in parts)
        {
            var t = p.Trim().Trim(',', ';', '(', ')');
            var key = ImportText.Key(t);
            if (key.Length < 2 || !t.Any(char.IsLetter)) continue;
            if (ImportText.Handings.Contains(t) || ImportText.IsFinish(t, null)) continue;
            if (seen.Add(key)) result.Add(t);
        }
        return result;
    }

    /// <summary>The comparison keys of a page's words, including the hyphen/slash parts of each word.</summary>
    /// <param name="text">Page text.</param>
    public static HashSet<string> WordKeys(string text)
    {
        var set = new HashSet<string>();
        foreach (var raw in Regex.Split(text ?? string.Empty, @"[\s,;()\[\]]+"))
        {
            if (raw.Length == 0) continue;
            var key = ImportText.Key(raw);
            if (key.Length > 0) set.Add(key);
            if (raw.IndexOfAny(new[] { '-', '/' }) >= 0)
                foreach (var part in raw.Split('-', '/'))
                {
                    var pk = ImportText.Key(part);
                    if (pk.Length > 0) set.Add(pk);
                }
        }
        return set;
    }

    /// <summary>
    /// Scores how well a page matches: the model as a whole word (best), as the start of a word
    /// ("4040XP-EDA"), or only somewhere in the text; plus each option code found, the word
    /// "template", and the model in the page heading. 0 = the model isn't on the page.
    /// </summary>
    /// <param name="text">Page text.</param>
    /// <param name="modelKey">The model's comparison key.</param>
    /// <param name="optionCodes">The callout's option codes.</param>
    public static int ScorePage(string text, string modelKey, IReadOnlyList<string> optionCodes)
    {
        if (string.IsNullOrEmpty(text) || modelKey.Length == 0) return 0;
        var words = WordKeys(text);
        int score;
        if (words.Contains(modelKey)) score = 60;
        else if (words.Any(w => w.StartsWith(modelKey, StringComparison.Ordinal) && w.Length <= modelKey.Length + 6)) score = 35;
        else if (ImportText.Key(text).Contains(modelKey, StringComparison.Ordinal)) score = 10;
        else return 0;

        foreach (var code in optionCodes)
            if (words.Contains(ImportText.Key(code))) score += 8;
        if (text.Contains("TEMPLATE", StringComparison.OrdinalIgnoreCase)) score += 15;
        var heading = string.Join(" ", Lines(text).Take(6));
        if (WordKeys(heading).Contains(modelKey)) score += 15;
        return score;
    }

    /// <summary>
    /// The template numbers a page gives: numbers written after "Template" / "Template No." /
    /// "Template #", and template numbers already in the database (<paramref name="knownNumbers"/>)
    /// that appear on the page (long ones anywhere; short ones only on a line that says template,
    /// or when they are the model itself). Known ones first.
    /// </summary>
    /// <param name="text">Page text.</param>
    /// <param name="modelKey">The model's comparison key.</param>
    /// <param name="knownNumbers">This manufacturer's template numbers in the database.</param>
    public static List<string> TemplateNumbers(string text, string modelKey, IEnumerable<string> knownNumbers)
    {
        var known = new List<string>();
        var written = new List<string>();
        if (string.IsNullOrEmpty(text)) return known;

        var lines = Lines(text);
        var allWords = WordKeys(text);
        var templateLineWords = WordKeys(string.Join("\n", lines.Where(l => l.Contains("TEMPL", StringComparison.OrdinalIgnoreCase))));
        foreach (var number in knownNumbers)
        {
            var key = ImportText.Key(number);
            if (key.Length < 3 || !key.Any(char.IsDigit)) continue;
            bool found = key.Length >= 5 ? allWords.Contains(key) : templateLineWords.Contains(key) || (key == modelKey && allWords.Contains(key));
            if (found && !known.Any(k => ImportText.Key(k) == key)) known.Add(number.Trim());
        }

        foreach (Match m in TemplateLabel.Matches(text))
        {
            var n = m.Groups["n"].Value.TrimEnd('.', '/', '-');
            var key = ImportText.Key(n);
            if (key.Length < 2 || NotNumbers.Contains(n)) continue;
            if (known.Any(k => ImportText.Key(k) == key) || written.Any(k => ImportText.Key(k) == key)) continue;
            written.Add(n);
        }
        known.AddRange(written);
        return known;
    }

    /// <summary>
    /// What an option code means on this page: the column after the code ("EDA  Extra Duty Arm"),
    /// or the text after "code - ..." / "code = ..." / "code: ...". Null when the page doesn't say.
    /// </summary>
    /// <param name="text">Page text.</param>
    /// <param name="code">The option code, e.g. "RW/PA".</param>
    public static string? OptionMeaning(string text, string code)
    {
        var codeKey = ImportText.Key(code);
        if (codeKey.Length < 2 || string.IsNullOrEmpty(text)) return null;
        foreach (var line in Lines(text))
        {
            var cells = Columns.Split(line.Trim()).Where(c => c.Length > 0).ToList();
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                // The whole cell is the code: the meaning is the next cell.
                if (ImportText.Key(cell) == codeKey && i + 1 < cells.Count)
                {
                    var meaning = Clean(Separator.Replace(cells[i + 1], string.Empty));
                    if (IsMeaning(meaning, codeKey)) return meaning;
                }
                // "EDA - Extra duty arm", "EDA = ...", "EDA: ..." or "EDA Extra Duty Arm" in one cell.
                var first = cell.Split(' ', 2);
                if (first.Length == 2 && ImportText.Key(first[0]) == codeKey && first[0].Any(char.IsLetter))
                {
                    var rest = first[1];
                    bool separated = Separator.IsMatch(rest);
                    var meaning = Clean(Separator.Replace(rest, string.Empty));
                    if ((separated || char.IsUpper(meaning.FirstOrDefault())) && IsMeaning(meaning, codeKey)) return meaning;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The product description on a page: the longest wording on the model's line (other than
    /// codes and prices), else the page heading (the first wording line near the top).
    /// </summary>
    /// <param name="text">Page text.</param>
    /// <param name="modelKey">The model's comparison key.</param>
    /// <param name="optionCodes">The callout's option codes (left out of the description).</param>
    public static string? Description(string text, string modelKey, IReadOnlyList<string> optionCodes)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var lines = Lines(text);
        var codeKeys = optionCodes.Select(ImportText.Key).ToHashSet();
        foreach (var line in lines)
        {
            if (!WordKeys(line).Contains(modelKey)) continue;
            var best = Columns.Split(line.Trim())
                .Select(c => Clean(RemoveWord(c, modelKey, codeKeys)))
                .Where(c => Words.Matches(c).Count >= 2 && !c.Contains('$'))
                .OrderByDescending(c => c.Length)
                .FirstOrDefault();
            if (best != null) return Shorten(best);
        }
        foreach (var line in lines.Take(8))
        {
            var cell = Columns.Split(line.Trim()).OrderByDescending(c => c.Length).FirstOrDefault() ?? string.Empty;
            int letters = cell.Count(char.IsLetter);
            if (Words.Matches(cell).Count >= 2 && letters >= cell.Length / 2 && cell.Length is >= 6 and <= 90 && !cell.Contains('$'))
                return Shorten(Clean(cell));
        }
        return null;
    }

    /// <summary>The lines around the first mention of the model (up to 5), for display.</summary>
    /// <param name="text">Page text.</param>
    /// <param name="modelKey">The model's comparison key.</param>
    public static string Snippet(string text, string modelKey)
    {
        var lines = Lines(text);
        int at = lines.FindIndex(l => WordKeys(l).Contains(modelKey));
        if (at < 0) at = lines.FindIndex(l => ImportText.Key(l).Contains(modelKey, StringComparison.Ordinal));
        if (at < 0) return string.Join("\n", lines.Take(3).Select(l => Columns.Replace(l.Trim(), "  ")));
        return string.Join("\n", lines.Skip(Math.Max(0, at - 1)).Take(5).Select(l => Columns.Replace(l.Trim(), "  ")));
    }

    /// <summary>
    /// The description (category) that best fits catalog wording: the deepest active description
    /// all of whose words appear in the text (plural/singular ignored). Null when none fits.
    /// </summary>
    /// <param name="descriptions">All descriptions.</param>
    /// <param name="text">Catalog wording (description, heading, snippet).</param>
    public static int? SuggestDescriptionId(IEnumerable<Description> descriptions, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var list = descriptions.ToList();
        var byId = list.ToDictionary(d => d.Id);
        var stems = Words.Matches(text).Select(m => Stem(m.Value)).ToHashSet();
        int? best = null;
        int bestDepth = -1, bestWords = 0;
        foreach (var d in list.Where(d => d.IsActive))
        {
            var words = Words.Matches(d.DescriptionText ?? string.Empty).Select(m => Stem(m.Value)).Where(w => w.Length >= 3).ToList();
            if (words.Count == 0 || !words.All(stems.Contains)) continue;
            int depth = 0;
            var seen = new HashSet<int>();
            for (int? p = d.ParentId; p is int pid && byId.TryGetValue(pid, out var parent) && seen.Add(pid); p = parent.ParentId) depth++;
            if (depth > bestDepth || (depth == bestDepth && words.Count > bestWords))
            {
                best = d.Id;
                bestDepth = depth;
                bestWords = words.Count;
            }
        }
        return best;
    }

    /// <summary>Splits page text into non-empty lines.</summary>
    /// <param name="text">Page text.</param>
    public static List<string> Lines(string text) =>
        (text ?? string.Empty).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();

    private static bool IsMeaning(string meaning, string codeKey) =>
        Words.Matches(meaning).Count >= 1 && meaning.Count(char.IsLetter) >= 3 && ImportText.Key(meaning) != codeKey && !meaning.StartsWith('$');

    private static string RemoveWord(string cell, string modelKey, HashSet<string> codeKeys) =>
        string.Join(" ", cell.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => { var k = ImportText.Key(w); return k != modelKey && !codeKeys.Contains(k) && !(k.Length > 0 && k.All(char.IsDigit)); }));

    private static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '–', '=', ':', ',', ';', '.', '*');

    private static string Shorten(string s) => s.Length <= 120 ? s : s[..117] + "...";

    private static string Stem(string word)
    {
        var w = word.ToLowerInvariant();
        if (w.EndsWith("ies") && w.Length > 4) return w[..^3] + "y";
        if (w.EndsWith("ses") || w.EndsWith("xes") || w.EndsWith("ches") || w.EndsWith("shes")) return w[..^2];
        if (w.EndsWith('s') && !w.EndsWith("ss") && w.Length > 3) return w[..^1];
        return w;
    }
}
