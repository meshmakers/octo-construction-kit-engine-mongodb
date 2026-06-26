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
                return objects.Select(o => o.ToString() ?? string.Empty).ToList();
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
                return [comparisonValue?.ToString() ?? string.Empty];
        }
    }
}
