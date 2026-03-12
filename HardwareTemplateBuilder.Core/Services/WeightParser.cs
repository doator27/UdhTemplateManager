using System;

namespace HardwareTemplateBuilder.Core.Services;

/// <summary>
/// Parses and compares weight strings in the "00.000.000" format used for template sorting.
/// The format is: {Function}.{Type}.{SubType} where each segment is a zero-padded integer.
/// Example: "01.002.015" — Function=01 (Hangs), Type=002, SubType=015.
/// </summary>
public class WeightParser
{
    /// <summary>
    /// Represents a parsed weight broken into its three numeric segments.
    /// </summary>
    public readonly struct ParsedWeight : IComparable<ParsedWeight>
    {
        /// <summary>Gets the function segment (first part, e.g. 01 = Hangs).</summary>
        public int Function { get; }

        /// <summary>Gets the type segment (second part, e.g. 001 = Mortise Lockset).</summary>
        public int Type { get; }

        /// <summary>Gets the subtype segment (third part, e.g. 020).</summary>
        public int SubType { get; }

        /// <summary>Initializes a new <see cref="ParsedWeight"/> with the three segments.</summary>
        public ParsedWeight(int function, int type, int subType)
        {
            Function = function;
            Type = type;
            SubType = subType;
        }

        /// <inheritdoc/>
        public int CompareTo(ParsedWeight other)
        {
            var functionCompare = Function.CompareTo(other.Function);
            if (functionCompare != 0) return functionCompare;

            var typeCompare = Type.CompareTo(other.Type);
            if (typeCompare != 0) return typeCompare;

            return SubType.CompareTo(other.SubType);
        }

        /// <inheritdoc/>
        public override string ToString() =>
            $"{Function:D2}.{Type:D3}.{SubType:D3}";
    }

    /// <summary>
    /// Parses a weight string in "00.000.000" format into a <see cref="ParsedWeight"/>.
    /// </summary>
    /// <param name="weightString">The weight string to parse (e.g. "01.002.015").</param>
    /// <returns>A <see cref="ParsedWeight"/> with the three numeric segments.</returns>
    /// <exception cref="FormatException">
    /// Thrown when the string does not match the expected "00.000.000" format.
    /// </exception>
    public ParsedWeight Parse(string weightString)
    {
        if (string.IsNullOrWhiteSpace(weightString))
            throw new FormatException("Weight string cannot be null or empty.");

        var parts = weightString.Split('.');
        if (parts.Length != 3)
            throw new FormatException(
                $"Weight string must have exactly 3 dot-delimited segments. Got: '{weightString}'");

        if (!int.TryParse(parts[0], out var function))
            throw new FormatException(
                $"Function segment '{parts[0]}' is not a valid integer in weight '{weightString}'.");

        if (!int.TryParse(parts[1], out var type))
            throw new FormatException(
                $"Type segment '{parts[1]}' is not a valid integer in weight '{weightString}'.");

        if (!int.TryParse(parts[2], out var subType))
            throw new FormatException(
                $"SubType segment '{parts[2]}' is not a valid integer in weight '{weightString}'.");

        return new ParsedWeight(function, type, subType);
    }

    /// <summary>
    /// Compares two weight strings, returning negative if <paramref name="a"/> sorts before <paramref name="b"/>,
    /// zero if equal, or positive if <paramref name="a"/> sorts after <paramref name="b"/>.
    /// </summary>
    /// <param name="a">First weight string.</param>
    /// <param name="b">Second weight string.</param>
    /// <returns>Comparison result consistent with <see cref="IComparable{T}"/>.</returns>
    public int Compare(string a, string b) => Parse(a).CompareTo(Parse(b));
}
