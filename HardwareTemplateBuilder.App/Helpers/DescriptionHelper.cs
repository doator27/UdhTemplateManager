using HardwareTemplateBuilder.Core.Models;
using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.App.Helpers;

/// <summary>
/// Item used to populate description combo boxes. Displays the full ancestor path
/// so that nodes with the same name but different parents are unambiguous.
/// </summary>
public class DescriptionComboItem
{
    /// <summary>Gets or sets the <see cref="Description"/> primary key.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the full display path, e.g. "Exit Device / Trim".</summary>
    public string DisplayText { get; set; } = string.Empty;
}

/// <summary>
/// Helper for flattening the description hierarchy into a depth-first sorted list
/// suitable for combo box display.
/// </summary>
public static class DescriptionHelper
{
    /// <summary>
    /// Flattens <paramref name="allDescriptions"/> into a depth-first list ordered by
    /// <c>SortOrder</c> (then alpha) within each sibling group. Each item's
    /// <see cref="DescriptionComboItem.DisplayText"/> shows the full ancestor path
    /// separated by " / " (e.g., "Exit Device / Trim").
    /// </summary>
    public static List<DescriptionComboItem> BuildComboItems(IEnumerable<Description> allDescriptions)
    {
        var all    = allDescriptions.ToList();
        var result = new List<DescriptionComboItem>();

        void Traverse(Description node, string prefix)
        {
            var path = string.IsNullOrEmpty(prefix)
                ? node.DescriptionText
                : $"{prefix} / {node.DescriptionText}";

            result.Add(new DescriptionComboItem { Id = node.Id, DisplayText = path });

            var children = all
                .Where(d => d.ParentId == node.Id)
                .OrderBy(d => d.SortOrder)
                .ThenBy(d => d.DescriptionText);

            foreach (var child in children)
                Traverse(child, path);
        }

        var roots = all
            .Where(d => d.ParentId == null)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.DescriptionText);

        foreach (var root in roots)
            Traverse(root, string.Empty);

        return result;
    }
}
