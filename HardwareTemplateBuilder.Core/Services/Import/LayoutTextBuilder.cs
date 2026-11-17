using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HardwareTemplateBuilder.Core.Services.Import;

/// <summary>
/// Turns positioned words into text lines that keep the page's column layout (like
/// <c>pdftotext -layout</c>): words on the same baseline become one line, each word is placed at
/// a column proportional to its x position, and a visible gap becomes two or more spaces. The
/// parsers rely on "2+ spaces = new column" and on continuation lines lining up with the column
/// above, so PDF text and OCR output can share one parser.
/// </summary>
public static class LayoutTextBuilder
{
    /// <summary>Builds the text lines for one page.</summary>
    /// <param name="words">The page's words.</param>
    public static List<string> BuildLines(IReadOnlyList<PdfWord> words)
    {
        var result = new List<string>();
        var usable = words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Right > w.Left && w.Bottom > w.Top).ToList();
        if (usable.Count == 0) return result;

        double charWidth = Median(usable.Select(w => (w.Right - w.Left) / Math.Max(1, w.Text.Length)));
        double height = Median(usable.Select(w => w.Bottom - w.Top));
        if (charWidth <= 0 || height <= 0) return result;

        // Group into lines by vertical centre.
        var lines = new List<List<PdfWord>>();
        List<PdfWord>? current = null;
        double currentCentre = 0;
        foreach (var w in usable.OrderBy(w => (w.Top + w.Bottom) / 2))
        {
            double centre = (w.Top + w.Bottom) / 2;
            if (current == null || Math.Abs(centre - currentCentre) > height * 0.6)
            {
                current = new List<PdfWord>();
                lines.Add(current);
            }
            current.Add(w);
            currentCentre = current.Average(x => (x.Top + x.Bottom) / 2);
        }

        foreach (var line in lines)
        {
            var sb = new StringBuilder();
            double prevRight = 0;
            foreach (var w in line.OrderBy(w => w.Left))
            {
                int col = (int)Math.Round(w.Left / charWidth);
                if (sb.Length > 0)
                {
                    bool bigGap = w.Left - prevRight > charWidth * 1.6;
                    col = bigGap ? Math.Max(col, sb.Length + 2) : sb.Length + 1;
                }
                if (col > sb.Length) sb.Append(' ', col - sb.Length);
                sb.Append(w.Text.Trim());
                prevRight = w.Right;
            }
            result.Add(sb.ToString());
        }
        return result;
    }

    private static double Median(IEnumerable<double> values)
    {
        var list = values.OrderBy(v => v).ToList();
        if (list.Count == 0) return 0;
        return list.Count % 2 == 1 ? list[list.Count / 2] : (list[list.Count / 2 - 1] + list[list.Count / 2]) / 2;
    }
}
