using System;
using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Parses page range specification strings into sorted lists of page numbers.
/// Supports single pages ("1"), ranges ("2-7"), non-consecutive lists ("3,6,8"),
/// and combinations ("1,3-5,8").
/// </summary>
public class PageRangeParser
{
    /// <summary>
    /// Parses a page range string into a sorted, deduplicated list of 1-based page numbers.
    /// </summary>
    /// <param name="pageRangeString">
    /// The page range string to parse. Examples: "1", "1-5", "1,3,5", "1,3-5,8".
    /// </param>
    /// <returns>A sorted list of page numbers with duplicates removed.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pageRangeString"/> is null.</exception>
    /// <exception cref="FormatException">Thrown when the string contains invalid syntax.</exception>
    public IReadOnlyList<int> Parse(string pageRangeString)
    {
        if (pageRangeString == null)
            throw new ArgumentNullException(nameof(pageRangeString));

        if (string.IsNullOrWhiteSpace(pageRangeString))
            return Array.Empty<int>();

        var pages = new HashSet<int>();
        var segments = pageRangeString.Split(',');

        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (trimmed.Contains('-'))
            {
                // Range segment: "3-7"
                var rangeParts = trimmed.Split('-');
                if (rangeParts.Length != 2)
                    throw new FormatException(
                        $"Invalid range segment '{trimmed}' in page range '{pageRangeString}'.");

                if (!int.TryParse(rangeParts[0].Trim(), out var start) || start < 1)
                    throw new FormatException(
                        $"Invalid range start '{rangeParts[0]}' in page range '{pageRangeString}'.");

                if (!int.TryParse(rangeParts[1].Trim(), out var end) || end < 1)
                    throw new FormatException(
                        $"Invalid range end '{rangeParts[1]}' in page range '{pageRangeString}'.");

                if (start > end)
                    throw new FormatException(
                        $"Range start ({start}) must be <= range end ({end}) in '{pageRangeString}'.");

                for (var i = start; i <= end; i++)
                    pages.Add(i);
            }
            else
            {
                // Single page segment: "3"
                if (!int.TryParse(trimmed, out var page) || page < 1)
                    throw new FormatException(
                        $"Invalid page number '{trimmed}' in page range '{pageRangeString}'.");

                pages.Add(page);
            }
        }

        return pages.OrderBy(p => p).ToList();
    }
}
