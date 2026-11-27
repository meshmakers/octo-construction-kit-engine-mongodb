using System.Text.Json.Nodes;

using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.RestoreSystemModelCheck;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RoundTrip;

/// <summary>
///     CK v2 round-trip gate (JSON view; the hard gate is CkModelReflectionComparer) (concept §4.6, the isRuntimeState lesson): compares a compiled model with
///     its MongoDB read-back by serializing BOTH to JSON over all public properties — no hand-written list of
///     fields. A property added to any CK DTO that the persistence layer does not write AND read back shows up
///     as a difference automatically, which is exactly the failure class of AB#4589 / AB#5187 / AB#5533.
///     <para>
///         The canonical form is <see cref="CkCompiledModelCanonicalizer" /> (shared with the restore check of
///         service-managed models): nulls and empty arrays are dropped, arrays of objects are sorted by their canonical
///         JSON, and the paths in <see cref="RoundTripIgnoredPaths" /> are removed.
///     </para>
/// </summary>
internal static class CkModelJsonComparer
{
    /// <summary>
    ///     JSON paths that are legitimately NOT persisted: the single list of
    ///     <see cref="CkCompiledModelCanonicalizer.IgnoredPaths" />, which the restore check of service-managed models
    ///     uses too. Every entry needs a justification; adding one is a design decision, not a way to make the gate green.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> RoundTripIgnoredPaths => CkCompiledModelCanonicalizer.IgnoredPaths;

    /// <summary>
    ///     Returns one line per difference (<c>path: expected ≠ actual</c>); empty when the read-back is equal.
    /// </summary>
    internal static IReadOnlyList<string> Compare(CkCompiledModelRoot expected, CkCompiledModelRoot actual)
    {
        var expectedNode = CkCompiledModelCanonicalizer.ToCanonicalNode(expected);
        var actualNode = CkCompiledModelCanonicalizer.ToCanonicalNode(actual);
        var differences = new List<string>();
        Diff(expectedNode, actualNode, "$", differences);
        return differences;
    }

    internal static string ToCanonicalJson(CkCompiledModelRoot model) => CkCompiledModelCanonicalizer.ToCanonicalJson(model);

    private static void Diff(JsonNode? expected, JsonNode? actual, string path, List<string> differences)
    {
        switch (expected)
        {
            case JsonObject expectedObject when actual is JsonObject actualObject:
                foreach (var key in expectedObject.Select(p => p.Key).Union(actualObject.Select(p => p.Key))
                             .OrderBy(k => k, StringComparer.Ordinal))
                {
                    Diff(expectedObject[key], actualObject[key], $"{path}.{key}", differences);
                }

                return;
            case JsonArray expectedArray when actual is JsonArray actualArray:
                if (expectedArray.Count != actualArray.Count)
                {
                    differences.Add($"{path}: {expectedArray.Count} elements ≠ {actualArray.Count} elements " +
                                    $"(expected {Short(expectedArray)}, actual {Short(actualArray)})");
                    return;
                }

                for (var i = 0; i < expectedArray.Count; i++)
                {
                    Diff(expectedArray[i], actualArray[i], $"{path}[{ElementLabel(expectedArray[i], i)}]",
                        differences);
                }

                return;
        }

        var expectedJson = expected?.ToJsonString();
        var actualJson = actual?.ToJsonString();
        if (expectedJson != actualJson)
        {
            differences.Add($"{path}: {Short(expected)} ≠ {Short(actual)}");
        }
    }

    private static string ElementLabel(JsonNode? element, int index)
    {
        if (element is JsonObject o)
        {
            foreach (var key in new[]
                     {
                         "typeId", "recordId", "enumId", "attributeId", "associationRoleId", "interfaceId", "methodId",
                         "attributeName", "name", "id", "code", "key", "ckRoleId"
                     })
            {
                if (o[key] is JsonValue value)
                {
                    return $"{key}={value.ToJsonString()}";
                }
            }
        }

        return index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Short(JsonNode? node)
    {
        var json = node?.ToJsonString() ?? "<absent>";
        return json.Length > 160 ? json[..160] + "…" : json;
    }
}
