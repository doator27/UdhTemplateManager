using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>One horizontal glyph of a PDF page (top-left origin, points).</summary>
/// <param name="Left">Where the glyph starts on its baseline.</param>
/// <param name="Right">Where the next glyph would start (left + advance).</param>
/// <param name="Baseline">Baseline y, measured from the top of the page.</param>
/// <param name="Size">Font size in points.</param>
/// <param name="Text">The glyph's text.</param>
public sealed record PdfLetter(double Left, double Right, double Baseline, double Size, string Text);

/// <summary>
/// Builds words from single glyphs. Some PDF writers (e.g. GemBox, used for component costing
/// reports) place every word by position and write no space characters at all, so a library's
/// own word splitting can glue a whole line together ("ConcealedCloser2033"). Here a word ends at
/// a space glyph, a new line, or a visible gap between glyphs (more than
/// <see cref="GapFactor"/> × the font size), which works whether or not the PDF has spaces.
/// </summary>
public static class PdfLetterWords
{
    /// <summary>A gap wider than this fraction of the font size separates two words.</summary>
    public const double GapFactor = 0.15;

    /// <summary>Groups glyphs into words.</summary>
    /// <param name="letters">The page's horizontal glyphs, in any order.</param>
    public static List<PdfWord> Group(IEnumerable<PdfLetter> letters)
    {
        var words = new List<PdfWord>();
        var list = letters.Where(l => l.Text.Length > 0 && l.Size > 0).ToList();
        if (list.Count == 0) return words;

        // Lines by baseline, then glyphs left to right.
        var lines = new List<List<PdfLetter>>();
        foreach (var letter in list.OrderBy(l => l.Baseline).ThenBy(l => l.Left))
        {
            var line = lines.LastOrDefault();
            if (line == null || Math.Abs(line[0].Baseline - letter.Baseline) > Math.Max(line[0].Size, letter.Size) * 0.3)
            {
                line = new List<PdfLetter>();
                lines.Add(line);
            }
            line.Add(letter);
        }

        foreach (var line in lines)
        {
            var sb = new StringBuilder();
            double left = 0, right = 0, top = 0, bottom = 0;

            void Flush()
            {
                if (sb.Length > 0) words.Add(new PdfWord(left, top, right, bottom, sb.ToString()));
                sb.Clear();
            }

            foreach (var g in line.OrderBy(l => l.Left))
            {
                if (string.IsNullOrWhiteSpace(g.Text))
                {
                    Flush();
                    continue;
                }
                if (sb.Length > 0 && g.Left - right > g.Size * GapFactor) Flush();
                if (sb.Length == 0)
                {
                    left = g.Left;
                    top = g.Baseline - g.Size * 0.75;
                    bottom = g.Baseline + g.Size * 0.25;
                }
                right = sb.Length == 0 ? Math.Max(g.Right, g.Left) : Math.Max(right, Math.Max(g.Right, g.Left));
                sb.Append(g.Text);
                top = Math.Min(top, g.Baseline - g.Size * 0.75);
                bottom = Math.Max(bottom, g.Baseline + g.Size * 0.25);
            }
            Flush();
        }
        return words;
    }
}
