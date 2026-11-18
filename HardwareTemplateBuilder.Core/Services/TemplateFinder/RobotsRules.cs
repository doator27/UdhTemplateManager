using System;
using System.Collections.Generic;
using System.Linq;

namespace HardwareTemplateBuilder.Core.Services.TemplateFinder;

/// <summary>
/// The "User-agent: *" rules of a site's robots.txt (Allow/Disallow path prefixes, with "*" and
/// "$" wildcards), so the template finder's crawl stays out of places a site asks bots to avoid.
/// Longest matching rule wins; Allow wins a tie.
/// </summary>
public sealed class RobotsRules
{
    private readonly List<(string Pattern, bool Allow)> _rules = new();

    /// <summary>Parses robots.txt text (null/empty = everything allowed).</summary>
    /// <param name="text">robots.txt content.</param>
    public static RobotsRules Parse(string? text)
    {
        var rules = new RobotsRules();
        if (string.IsNullOrWhiteSpace(text)) return rules;

        bool inStarGroup = false, lastWasAgent = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (field == "user-agent")
            {
                bool star = value == "*";
                inStarGroup = lastWasAgent ? inStarGroup || star : star;
                lastWasAgent = true;
                continue;
            }
            lastWasAgent = false;
            if (!inStarGroup) continue;
            if (field == "disallow" && value.Length > 0) rules._rules.Add((value, false));
            else if (field == "allow" && value.Length > 0) rules._rules.Add((value, true));
        }
        return rules;
    }

    /// <summary>True when the path (or full URL) may be fetched.</summary>
    /// <param name="pathOrUrl">A path such as "/products/x" or a full URL.</param>
    public bool IsAllowed(string pathOrUrl)
    {
        var path = pathOrUrl;
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var u)) path = u.PathAndQuery;
        int bestLength = -1;
        bool allowed = true;
        foreach (var (pattern, allow) in _rules)
        {
            if (!Matches(path, pattern)) continue;
            if (pattern.Length > bestLength || (pattern.Length == bestLength && allow))
            {
                bestLength = pattern.Length;
                allowed = allow;
            }
        }
        return allowed;
    }

    private static bool Matches(string path, string pattern)
    {
        bool anchored = pattern.EndsWith('$');
        var parts = (anchored ? pattern[..^1] : pattern).Split('*');
        int pos = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i == 0)
            {
                if (!path.StartsWith(part, StringComparison.Ordinal)) return false;
                pos = part.Length;
                continue;
            }
            int at = path.IndexOf(part, pos, StringComparison.Ordinal);
            if (at < 0) return false;
            pos = at + part.Length;
        }
        return !anchored || pos == path.Length || (parts.Length > 1 && parts[^1].Length == 0);
    }
}
