using System.Collections.Generic;
using System.IO;
using HardwareTemplateBuilder.Core.Models;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Builds a relative folder path for a template by walking its description's ancestry
/// from the root down. Used to organise stored template PDFs into a human-navigable
/// hierarchy under the manufacturer folder.
/// </summary>
public static class DescriptionPathService
{
    private static readonly HashSet<char> _invalid =
        new(Path.GetInvalidFileNameChars()) { ':', '/', '\\' };

    /// <summary>
    /// Returns a relative folder path representing the full description hierarchy for
    /// <paramref name="descriptionId"/>, e.g. <c>Hinges\Butt Hinges</c> (root first).
    /// Returns an empty string when <paramref name="descriptionId"/> is null or not found,
    /// meaning the template should be stored directly under its manufacturer folder.
    /// </summary>
    /// <param name="descriptionId">
    /// The ID of the leaf description node, or null for templates with no description.
    /// </param>
    /// <param name="allDescriptions">
    /// Flat list of all description records — loaded once by the caller to avoid repeated queries.
    /// </param>
    public static string GetFolderPath(int? descriptionId, IReadOnlyList<Description> allDescriptions)
    {
        if (descriptionId == null)
            return string.Empty;

        var lookup = new Dictionary<int, Description>(allDescriptions.Count);
        foreach (var d in allDescriptions)
            lookup[d.Id] = d;

        // Walk from leaf to root, collecting sanitized segment names.
        var segments = new List<string>();
        var currentId = descriptionId.Value;
        while (lookup.TryGetValue(currentId, out var node))
        {
            segments.Add(SanitizeName(node.DescriptionText));
            if (node.ParentId == null) break;
            currentId = node.ParentId.Value;
        }

        if (segments.Count == 0)
            return string.Empty;

        // Reverse so the path reads root → leaf.
        segments.Reverse();
        return Path.Combine(segments.ToArray());
    }

    /// <summary>
    /// Replaces characters that are illegal in folder names on Windows or Linux with underscores.
    /// </summary>
    private static string SanitizeName(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (_invalid.Contains(chars[i]))
                chars[i] = '_';
        }
        return new string(chars);
    }
}
