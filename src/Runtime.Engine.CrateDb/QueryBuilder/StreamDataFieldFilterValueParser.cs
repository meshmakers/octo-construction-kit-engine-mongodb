using System.Globalization;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

/// <summary>
/// Parses the <c>ComparisonValue</c> of an <c>In</c> / <c>NotIn</c> stream-data field filter
/// into the individual list values the CrateDB query builder expects.
///
/// This deliberately mirrors the MongoDB runtime-model parsing in
/// <c>RtFieldFilterResolver.ResolveSearchAttributeValue</c> so that the exact same GraphQL
/// <c>comparisonValue</c> syntax works against CrateDB archives and Mongo runtime entities —
/// the wire contract must not differ between the two engines:
/// <list type="bullet">
///   <item>a value that is already an enumerable of strings/objects is used as-is;</item>
///   <item>a string in array form (<c>"[a, b, c]"</c>) is unwrapped — surrounding brackets are
///     trimmed, the remainder is split on commas, and each item is whitespace- and
///     quote-trimmed, dropping empty entries;</item>
///   <item>any other scalar string is treated as a single value.</item>
/// </list>
/// The bracket trim uses <see cref="string.Trim(char[])"/>, so accidentally multi-wrapped
/// values such as <c>"[[[[a, b]]]]"</c> still reduce to <c>[a, b]</c>.
/// </summary>
public static class StreamDataFieldFilterValueParser
{
    public static List<string> ParseInValues(object? comparisonValue)
    {
        switch (comparisonValue)
        {
            case IEnumerable<string> strings:
                return strings.ToList();
            case IEnumerable<object> objects:
                return objects.Select(FormatScalar).ToList();
            case string text when text.StartsWith('[') && text.EndsWith(']'):
                var parsed = text.Trim('[', ']')
                    .Split(',')
                    .Select(x => x.Trim().Trim('"'))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
                // Fall back to the raw string when the array form is empty so the caller never
                // emits an invalid `IN ()`; a non-matching single value is the safe outcome.
                return parsed.Count > 0 ? parsed : [text];
            case string text:
                return [text];
            default:
                return [FormatScalar(comparisonValue)];
        }
    }

    /// <summary>
    /// Renders a filter comparison value as the text CrateDB has to parse back, using the
    /// invariant culture.
    /// </summary>
    /// <remarks>
    /// A plain <c>ToString()</c> formats with the AMBIENT culture, which CrateDB cannot read:
    /// a <see cref="DateTime" /> rendered on an en-US host becomes
    /// <c>09/30/2025 22:00:00</c> and the statement fails with
    /// <c>Cannot cast '…' of type `text` to type `timestamp with time zone`</c>; a
    /// <see cref="double" /> on a de-AT host becomes <c>0,08</c> and silently changes meaning.
    /// This is reachable from ordinary pipeline input: a JSON string in ISO-8601 form is boxed
    /// as a <see cref="DateTime" /> on the way in (JsonScalar.ToClr parses date strings for
    /// Newtonsoft parity), so a pipeline that passes an ISO timestamp as a field-filter
    /// comparison value never reaches the database as one unless it is rendered here.
    /// Dates use the round-trip format so the offset survives.
    /// </remarks>
    public static string FormatScalar(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}
