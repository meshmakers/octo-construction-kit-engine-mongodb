using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RoundTrip;

/// <summary>
///     CK v2 round-trip gate (JSON view; the hard gate is CkModelReflectionComparer) (concept §4.6, the isRuntimeState lesson): compares a compiled model with
///     its MongoDB read-back by serializing BOTH to JSON over all public properties — no hand-written list of
///     fields. A property added to any CK DTO that the persistence layer does not write AND read back shows up
///     as a difference automatically, which is exactly the failure class of AB#4589 / AB#5187 / AB#5533.
///     <para>
///         Canonicalization: nulls and empty arrays are dropped (they mean the same), arrays of objects are
///         sorted by their canonical JSON (Mongo returns rows in no defined order), and the paths in
///         <see cref="RoundTripIgnoredPaths" /> are removed.
///     </para>
/// </summary>
internal static class CkModelJsonComparer
{
    /// <summary>
    ///     JSON paths (wildcard <c>*</c> = any array element) that are legitimately NOT persisted. Every entry
    ///     needs a justification. Adding an entry is a design decision, not a way to make the gate green.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> RoundTripIgnoredPaths = new Dictionary<string, string>
    {
        // Serialization metadata of the compiled file, not model content.
        ["$.$schema"] = "schema URI of the compiled file",
        // Migrations are consumed during import (CompiledModelCkMigrationContentProvider) and never stored.
        ["$.migrations"] = "migration scripts are executed on import, not persisted",

        // PRE-EXISTING gap found by this gate (not CK v2): CkType.Indexes is persisted on the entity and consumed
        // from there (UpdateIndexAsync / AnalyseIndex), but TryLookupCkModelAsync has never mapped it back, so the
        // runtime graph carries no type indexes (only visible to MCP schema discovery and the text-index merge).
        // Reading it back changes runtime resolution for every tenant, so it is reported and not changed in Phase 1.
        ["$.types[*].indexes"] = "pre-existing: type indexes are not read back (reported to the lead)"
    };

    /// <summary>
    ///     Paths whose scalar values are compared by their invariant text: <c>ICollection&lt;object&gt;</c> members
    ///     that the import deliberately converts to the attribute's value type
    ///     (<c>AttributeValueConverter.ConvertAttributeValue</c>), while the catalog JSON yields them as strings —
    ///     <c>"0"</c> in the compiled model is the same default as <c>0</c> in the read-back.
    /// </summary>
    private static readonly string[] ScalarTextMembers = [".defaultValues[*]", ".autoCompleteValues[*]"];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    ///     Returns one line per difference (<c>path: expected ≠ actual</c>); empty when the read-back is equal.
    /// </summary>
    internal static IReadOnlyList<string> Compare(CkCompiledModelRoot expected, CkCompiledModelRoot actual)
    {
        var expectedNode = Canonicalize(JsonSerializer.SerializeToNode(expected, Options), "$");
        var actualNode = Canonicalize(JsonSerializer.SerializeToNode(actual, Options), "$");
        var differences = new List<string>();
        Diff(expectedNode, actualNode, "$", differences);
        return differences;
    }

    internal static string ToCanonicalJson(CkCompiledModelRoot model) =>
        Canonicalize(JsonSerializer.SerializeToNode(model, Options), "$")?.ToJsonString() ?? "null";

    private static JsonNode? Canonicalize(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject jsonObject:
            {
                var result = new JsonObject();
                foreach (var (key, value) in jsonObject.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    var childPath = $"{path}.{key}";
                    if (IsIgnored(childPath))
                    {
                        continue;
                    }

                    var canonical = Canonicalize(value, childPath);
                    if (canonical == null || canonical is JsonArray { Count: 0 })
                    {
                        continue;
                    }

                    result[key] = canonical;
                }

                return result;
            }
            case JsonArray jsonArray:
            {
                var items = jsonArray.Select(i => Canonicalize(i, $"{path}[*]")).ToList();
                if (items.All(i => i is JsonObject))
                {
                    items = items.OrderBy(i => i!.ToJsonString(), StringComparer.Ordinal).ToList();
                }

                return new JsonArray(items.ToArray());
            }
            case JsonValue jsonValue when ScalarTextMembers.Any(path.EndsWith):
                return JsonValue.Create(jsonValue.GetValueKind() == JsonValueKind.String
                    ? jsonValue.GetValue<string>()
                    : jsonValue.ToJsonString());
            default:
                return node?.DeepClone();
        }
    }

    private static bool IsIgnored(string path) => RoundTripIgnoredPaths.ContainsKey(path);

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
