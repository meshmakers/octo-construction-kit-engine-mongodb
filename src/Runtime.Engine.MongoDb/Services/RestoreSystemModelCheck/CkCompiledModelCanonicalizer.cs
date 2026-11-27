using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using System.Globalization;

using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.RestoreSystemModelCheck;

/// <summary>
///     The canonical JSON form of a compiled CK model as far as MongoDB persists it: a compiled model from a catalog and
///     the read-back of the same model from a tenant database have the same canonical form. Used by the restore check of
///     service-managed models (a restored model is genuine when its canonical form equals the catalog's) and by the
///     round-trip gate of the integration tests, so a DTO member the persistence layer does not store breaks that gate
///     before it can make the restore check refuse genuine dumps.
/// </summary>
/// <remarks>
///     Every public property of the DTOs is serialized, no hand-written field list. Canonicalization: nulls and empty
///     arrays are dropped (they mean the same), object keys are ordered, arrays of objects are ordered by their
///     canonical JSON (MongoDB returns rows in no defined order), the members of <see cref="IgnoredPaths" /> are removed,
///     attribute default values are converted to their value type like the import does, and the values of
///     <see cref="ScalarTextMembers" /> are compared by their invariant text.
/// </remarks>
internal static class CkCompiledModelCanonicalizer
{
    /// <summary>
    ///     JSON paths (wildcard <c>*</c> = any array element) that are not persisted in a tenant database, each with the
    ///     reason. Adding an entry is a design decision: a path listed here is not compared by the restore check either.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> IgnoredPaths = new Dictionary<string, string>
    {
        // Serialization metadata of the compiled file, not model content.
        ["$.$schema"] = "schema URI of the compiled file",
        // Migrations are consumed during import (CompiledModelCkMigrationContentProvider) and never stored.
        ["$.migrations"] = "migration scripts are executed on import, not persisted",
        // Acknowledgements of the author are build and publish-time metadata (the publish gate reads them from the
        // catalog JSON), a tenant never needs them.
        ["$.compatibility"] = "compatibility.acknowledge is build and publish-time metadata, not needed in tenants"
        // "$.types[*].indexes" is read back and compared like every other member; do not list it.
    };

    /// <summary>
    ///     Paths whose scalar values are compared by their invariant text: <c>ICollection&lt;object&gt;</c> members that the
    ///     import converts to the attribute's value type, while the catalog JSON yields them as strings - <c>"0"</c> in the
    ///     compiled model is the same default as <c>0</c> in the read-back.
    /// </summary>
    private static readonly string[] ScalarTextMembers = [".defaultValues[*]", ".autoCompleteValues[*]"];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The canonical JSON node of <paramref name="model" />.</summary>
    internal static JsonNode? ToCanonicalNode(CkCompiledModelRoot model)
    {
        var node = JsonSerializer.SerializeToNode(model, Options);
        NormalizeAttributeDefaultValues(node);
        return Canonicalize(node, "$");
    }

    /// <summary>
    ///     The import stores the default values of an attribute converted to its value type
    ///     (<see cref="AttributeValueConverter.ConvertAttributeValue" />): a <c>TimeSpan</c> default <c>"00:05:00"</c> of the
    ///     catalog is stored as its ticks, a <c>DateTime</c> as a date. Both sides are put through the same conversion and
    ///     written as invariant text, so a converted value compares equal to its catalog form. A value the converter
    ///     refuses stays as it is (and then differs).
    /// </summary>
    private static void NormalizeAttributeDefaultValues(JsonNode? root)
    {
        if (root is not JsonObject model || model["attributes"] is not JsonArray attributes)
        {
            return;
        }

        foreach (var attribute in attributes.OfType<JsonObject>())
        {
            if (attribute["defaultValues"] is not JsonArray values ||
                attribute["valueType"] is not JsonValue valueTypeNode ||
                !Enum.TryParse<AttributeValueTypesDto>(valueTypeNode.ToString(), out var valueType))
            {
                continue;
            }

            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] is not JsonValue value)
                {
                    continue;
                }

                object raw = value.GetValueKind() switch
                {
                    JsonValueKind.String => value.GetValue<string>(),
                    JsonValueKind.Number when value.TryGetValue<long>(out var integer) => integer,
                    JsonValueKind.Number => value.GetValue<double>(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => value.ToJsonString()
                };
                try
                {
                    values[i] = JsonValue.Create(InvariantText(AttributeValueConverter.ConvertAttributeValue(valueType, raw)));
                }
                catch (Exception)
                {
                    // Not convertible: the raw value stays and is compared as it is.
                }
            }
        }
    }

    private static string? InvariantText(object? value) => value switch
    {
        null => null,
        TimeSpan timeSpan => timeSpan.Ticks.ToString(CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    /// <summary>The canonical JSON text of <paramref name="model" />.</summary>
    internal static string ToCanonicalJson(CkCompiledModelRoot model) => ToCanonicalNode(model)?.ToJsonString() ?? "null";

    /// <summary>
    ///     The SHA-256 of the canonical JSON as lower-case hex. It names a content without carrying it, so a log line or a
    ///     refusal may show (a prefix of) it.
    /// </summary>
    internal static string ContentHash(CkCompiledModelRoot model) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson(model))));

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
                    if (IgnoredPaths.ContainsKey(childPath))
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
}
