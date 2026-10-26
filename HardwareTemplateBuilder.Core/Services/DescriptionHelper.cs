using System.Collections.Generic;
using System.Linq;
using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Item used to populate description combo/list boxes. Displays the full ancestor path
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
/// Helper for flattening the description hierarchy into a depth-first sorted list suitable
/// for combo/list box display. Shared by <c>HardwareTemplateBuilder.App</c> and
/// <c>HardwareTemplateBuilder.Lookup</c> — both projects reference this Core project, so this
/// is written once here rather than duplicated per project (the Lookup app cannot reference
/// the App project directly).
/// </summary>
public static class DescriptionHelper
{
    /// <summary>
    /// Flattens <paramref name="allDescriptions"/> into a depth-first list ordered by
    /// <c>SortOrder</c> (then alpha) within each sibling group. Each item's
    /// <see cref="DescriptionComboItem.DisplayText"/> shows the full ancestor path
    /// separated by " / " (e.g., "Exit Device / Trim").
    /// </summary>
    /// <param name="allDescriptions">Every description in the hierarchy.</param>
    /// <param name="activeOnly">
    /// When true, inactive descriptions are excluded entirely \u2014 used for pickers where
    /// inactive descriptions (and therefore their hardware items) should not be discoverable.
    /// </param>
    public static List<DescriptionComboItem> BuildComboItems(
        IEnumerable<Description> allDescriptions,
        bool activeOnly = false)
    {
        var all    = allDescriptions.ToList();
        var result = new List<DescriptionComboItem>();

        void Traverse(Description node, string prefix)
        {
            var path = string.IsNullOrEmpty(prefix)
                ? node.DescriptionText
                : $"{prefix} / {node.DescriptionText}";

            if (!activeOnly || node.IsActive)
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

    /// <summary>
    /// Same as <see cref="BuildComboItems"/>, but only includes leaf nodes (descriptions with
    /// no children) — intermediate parent categories are walked for path-building purposes but
    /// excluded from the result, since they're not meaningful hardware-item descriptions on
    /// their own.
    /// </summary>
    /// <param name="allDescriptions">Every description in the hierarchy.</param>
    /// <param name="activeOnly">
    /// When true, inactive descriptions are excluded entirely — used for search contexts where
    /// inactive descriptions (and therefore their hardware items) should not be discoverable.
    /// </param>
    public static List<DescriptionComboItem> BuildLeafComboItems(
        IEnumerable<Description> allDescriptions,
        bool activeOnly = false)
    {
        var all    = allDescriptions.ToList();
        var result = new List<DescriptionComboItem>();

        void Traverse(Description node, string prefix)
        {
            var path = string.IsNullOrEmpty(prefix)
                ? node.DescriptionText
                : $"{prefix} / {node.DescriptionText}";

            var children = all
                .Where(d => d.ParentId == node.Id)
                .OrderBy(d => d.SortOrder)
                .ThenBy(d => d.DescriptionText)
                .ToList();

            if (children.Count == 0)
            {
                if (!activeOnly || node.IsActive)
                    result.Add(new DescriptionComboItem { Id = node.Id, DisplayText = path });
                return;
            }

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

    /// <summary>
    /// Same as <see cref="BuildLeafComboItems"/>, but also includes any node listed in
    /// <paramref name="descriptionIdsInUse"/> even if it has children. This covers
    /// descriptions that gained child nodes after hardware items were already assigned to
    /// them (so they're no longer true leaves), ensuring those items' description still
    /// appears in leaf-filtered pickers instead of silently disappearing.
    /// </summary>
    /// <param name="allDescriptions">Every description in the hierarchy.</param>
    /// <param name="descriptionIdsInUse">Description IDs actually referenced by hardware items.</param>
    /// <param name="activeOnly">
    /// When true, inactive descriptions are excluded entirely — used for search contexts where
    /// inactive descriptions (and therefore their hardware items) should not be discoverable.
    /// </param>
    public static List<DescriptionComboItem> BuildSelectableComboItems(
        IEnumerable<Description> allDescriptions,
        ISet<int> descriptionIdsInUse,
        bool activeOnly = false)
    {
        var all    = allDescriptions.ToList();
        var result = new List<DescriptionComboItem>();

        void Traverse(Description node, string prefix)
        {
            var path = string.IsNullOrEmpty(prefix)
                ? node.DescriptionText
                : $"{prefix} / {node.DescriptionText}";

            var children = all
                .Where(d => d.ParentId == node.Id)
                .OrderBy(d => d.SortOrder)
                .ThenBy(d => d.DescriptionText)
                .ToList();

            if ((children.Count == 0 || descriptionIdsInUse.Contains(node.Id)) && (!activeOnly || node.IsActive))
                result.Add(new DescriptionComboItem { Id = node.Id, DisplayText = path });

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
