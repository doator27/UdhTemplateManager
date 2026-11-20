using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Type-to-jump for lists, drop-downs and trees (both apps): letters typed in quick succession
/// build up a search ("v", "vo" → "Von Duprin"); a pause of <see cref="ResetAfter"/> starts a new
/// one. Pressing the same letter again cycles through the items starting with it. Matching ignores
/// case and is on the start of the item's text, then on the start of any word in it.
/// </summary>
public sealed class TypeAheadSearch
{
    /// <summary>How long a pause starts a new search.</summary>
    public static readonly TimeSpan ResetAfter = TimeSpan.FromSeconds(1);

    private static readonly string[] TextProperties =
    {
        "ManufacturerName", "DescriptionText", "DisplayName", "Name", "Title", "Text", "Label", "ModelNumber",
        "TemplateNumber", "JobNumber", "UserName", "FileName", "Catalog", "Header",
    };

    private string _buffer = string.Empty;
    private DateTime _last = DateTime.MinValue;

    /// <summary>Gets the current search text.</summary>
    public string Buffer => _buffer;

    /// <summary>Adds typed text to the search (starting over after a pause) and returns the search.</summary>
    /// <param name="text">The typed text.</param>
    /// <param name="now">The time it was typed.</param>
    public string Append(string text, DateTime now)
    {
        if (now - _last > ResetAfter) _buffer = string.Empty;
        _last = now;
        _buffer += text;
        return _buffer;
    }

    /// <summary>
    /// The index of the item to jump to, or -1. A one-letter (or same-letter-repeated) search
    /// moves to the next item after <paramref name="current"/> starting with that letter (cycling);
    /// a longer search finds the first match from <paramref name="current"/> on (wrapping), so the
    /// selection stays put while it still matches.
    /// </summary>
    /// <param name="texts">The items' texts, in display order.</param>
    /// <param name="current">The current item (-1 = none).</param>
    /// <param name="search">The search text.</param>
    public static int Find(IReadOnlyList<string?> texts, int current, string search)
    {
        if (texts.Count == 0 || string.IsNullOrEmpty(search)) return -1;
        bool sameLetter = search.Length > 0 && search.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(search[0]));
        string key = sameLetter ? search[..1] : search;
        int start = sameLetter ? current + 1 : Math.Max(0, current);

        int hit = Scan(texts, start, t => t.StartsWith(key, StringComparison.OrdinalIgnoreCase));
        if (hit < 0 && sameLetter && search.Length > 1)
        {
            // "oo" may be a real start ("Oo..."): try the whole text.
            hit = Scan(texts, Math.Max(0, current), t => t.StartsWith(search, StringComparison.OrdinalIgnoreCase));
        }
        if (hit < 0)
            hit = Scan(texts, start, t => t.Split(new[] { ' ', '-', '/', '>', '(' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(w => w.StartsWith(key, StringComparison.OrdinalIgnoreCase)));
        return hit;
    }

    private static int Scan(IReadOnlyList<string?> texts, int start, Func<string, bool> match)
    {
        for (int k = 0; k < texts.Count; k++)
        {
            int i = ((start % texts.Count) + texts.Count + k) % texts.Count;
            var t = texts[i]?.Trim();
            if (!string.IsNullOrEmpty(t) && match(t)) return i;
        }
        return -1;
    }

    /// <summary>
    /// The text a list item shows: itself for strings, the list's display member (a property path)
    /// when given, its own ToString when the type overrides it, else a common name property
    /// (ManufacturerName, DescriptionText, Name, ModelNumber...). Null when none is found.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="memberPath">The list's display member path, if any.</param>
    public static string? ItemText(object? item, string? memberPath = null)
    {
        if (item == null) return null;
        if (item is string s) return s;
        if (!string.IsNullOrWhiteSpace(memberPath))
        {
            object? cur = item;
            foreach (var part in memberPath.Split('.'))
                cur = cur?.GetType().GetProperty(part, BindingFlags.Public | BindingFlags.Instance)?.GetValue(cur);
            if (cur != null) return cur.ToString();
        }
        var type = item.GetType();
        var toString = type.GetMethod(nameof(ToString), Type.EmptyTypes);
        // A ToString the type wrote itself (not object's, not a record's generated "X { A = … }").
        if (toString != null && toString.DeclaringType != typeof(object) && toString.DeclaringType != typeof(ValueType)
            && !toString.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)
            && !(type.IsGenericType && type.Name.StartsWith("ValueTuple", StringComparison.Ordinal)))
            return item.ToString();
        foreach (var name in TextProperties)
        {
            var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.GetIndexParameters().Length == 0 && p.GetValue(item) is { } v && v is not System.Collections.IEnumerable or string)
                return v.ToString();
        }
        return null;
    }
}
