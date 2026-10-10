using System.Collections;
using System.Globalization;
using System.Reflection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RoundTrip;

/// <summary>
///     CK v2 F1.3-S3 (AB#5916, concept §4.6 guard 1): deep comparison of a compiled model with its MongoDB read-back
///     by REFLECTION over every public instance property of the DTO graph (<c>CkCompiledModelRoot</c> and every
///     nested DTO), using the runtime types of both sides. A property that exists on the compiled side but is not
///     persisted and read back shows up as a difference — no hand-written field list, no serializer settings that
///     could hide a property (unlike the JSON comparer, which only sees what the JSON options emit).
///     <para>
///         Rules: a "DTO" is any class whose name ends in <c>Dto</c> or <c>Root</c> and is walked property by
///         property; everything else (ids, versions, enums, primitives, <c>object</c> default values) is a scalar and
///         compared by its invariant text. <c>null</c> equals an empty collection. Collections of DTOs are matched by
///         their identity property (<see cref="IdentityProperties" />), so the order MongoDB returns rows in does not
///         matter; collections of scalars are compared in order (the declared order is part of the model, e.g.
///         <c>implements</c>, L20).
///     </para>
/// </summary>
internal static class CkModelReflectionComparer
{
    /// <summary>
    ///     Properties that are legitimately NOT persisted, keyed <c>DeclaringType.Property</c>. Every entry needs a
    ///     reason; adding one is a design decision reviewed like code.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> AllowList = new Dictionary<string, string>
    {
        ["CkCompiledModelRoot.Migrations"] =
            "migration scripts are executed during the import (CompiledModelCkMigrationContentProvider), not stored",
        ["CkCompiledModelRoot.Compatibility"] =
            "AB#6295: the author's compatibility.acknowledge entries are build and publish-time metadata; the publish " +
            "gate reads them from the catalog JSON of the compiled model, a tenant never needs them",
        ["CkTypeDto.Indexes"] =
            "pre-existing: type indexes are stored on the CkType entity and consumed from there (UpdateIndexAsync), " +
            "but have never been read back; reading them back changes runtime resolution for every tenant (reported " +
            "by the round-trip gate, decision pending)"
    };

    /// <summary>Identity properties of collection elements, in order of preference.</summary>
    private static readonly string[] IdentityProperties =
    [
        "TypeId", "RecordId", "EnumId", "AttributeId", "AssociationRoleId", "InterfaceId", "MethodId", "CkRoleId",
        "CkAttributeId", "AttributeName", "Name", "Key", "Code", "Range"
    ];

    internal static IReadOnlyList<string> Compare(object expected, object actual)
    {
        var differences = new List<string>();
        Walk(expected, actual, "$", differences);
        return differences;
    }

    private static void Walk(object? expected, object? actual, string path, List<string> differences)
    {
        if (IsEmpty(expected) && IsEmpty(actual))
        {
            return;
        }

        if (expected == null || actual == null)
        {
            differences.Add($"{path}: {Describe(expected)} ≠ {Describe(actual)}");
            return;
        }

        if (expected is IEnumerable expectedItems and not string && actual is IEnumerable actualItems and not string)
        {
            WalkCollection(expectedItems.Cast<object?>().ToList(), actualItems.Cast<object?>().ToList(), path,
                differences);
            return;
        }

        if (!IsDto(expected.GetType()) || !IsDto(actual.GetType()))
        {
            if (Scalar(expected) != Scalar(actual))
            {
                differences.Add($"{path}: {Describe(expected)} ≠ {Describe(actual)}");
            }

            return;
        }

        foreach (var property in PropertiesOf(expected.GetType()).Concat(PropertiesOf(actual.GetType()))
                     .GroupBy(p => p.Name).Select(g => g.First()))
        {
            var key = $"{property.DeclaringType!.Name}.{property.Name}";
            if (AllowList.ContainsKey(key))
            {
                continue;
            }

            var expectedValue = ValueOf(expected, property.Name);
            var actualValue = ValueOf(actual, property.Name);
            if (expectedValue is Missing || actualValue is Missing)
            {
                // The property exists only on one side's runtime type: e.g. a DTO subclass with a new member that
                // the persistence layer does not know.
                if (!IsEmpty(expectedValue is Missing ? null : expectedValue) ||
                    !IsEmpty(actualValue is Missing ? null : actualValue))
                {
                    differences.Add($"{path}.{property.Name}: only on the " +
                                    (expectedValue is Missing ? "read-back" : "compiled") + " side " +
                                    $"({Describe(expectedValue is Missing ? actualValue : expectedValue)})");
                }

                continue;
            }

            Walk(expectedValue, actualValue, $"{path}.{property.Name}", differences);
        }
    }

    private static void WalkCollection(List<object?> expected, List<object?> actual, string path,
        List<string> differences)
    {
        if (expected.Count != actual.Count)
        {
            differences.Add($"{path}: {expected.Count} elements ≠ {actual.Count} elements " +
                            $"([{string.Join(", ", expected.Select(Identity))}] vs [{string.Join(", ", actual.Select(Identity))}])");
            return;
        }

        if (expected.All(e => e != null && IsDto(e.GetType())))
        {
            var actualByIdentity = actual.GroupBy(Identity).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var item in expected)
            {
                var identity = Identity(item);
                if (!actualByIdentity.TryGetValue(identity, out var candidates) || candidates.Count == 0)
                {
                    differences.Add($"{path}[{identity}]: missing in the read-back");
                    continue;
                }

                var match = candidates[0];
                candidates.RemoveAt(0);
                Walk(item, match, $"{path}[{identity}]", differences);
            }

            return;
        }

        for (var i = 0; i < expected.Count; i++)
        {
            Walk(expected[i], actual[i], $"{path}[{i}]", differences);
        }
    }

    private static string Identity(object? item)
    {
        if (item == null || !IsDto(item.GetType()))
        {
            return Scalar(item);
        }

        foreach (var name in IdentityProperties)
        {
            var value = ValueOf(item, name);
            if (value is not Missing && value != null)
            {
                return $"{name}={Scalar(value)}";
            }
        }

        return item.GetType().Name;
    }

    private static bool IsDto(Type type) =>
        type.IsClass && type != typeof(string) && (type.Name.EndsWith("Dto", StringComparison.Ordinal) ||
                                                   type.Name.EndsWith("Root", StringComparison.Ordinal));

    private static IEnumerable<PropertyInfo> PropertiesOf(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);

    private static object? ValueOf(object owner, string propertyName)
    {
        var property = owner.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        return property == null || property.GetIndexParameters().Length > 0 ? Missing.Value : property.GetValue(owner);
    }

    private static bool IsEmpty(object? value) =>
        value == null || (value is IEnumerable e and not string && !e.Cast<object?>().Any());

    private static string Scalar(object? value) => value switch
    {
        null => "<null>",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "<null>"
    };

    private static string Describe(object? value)
    {
        var text = value is Missing ? "<absent>" : Scalar(value);
        return text.Length > 120 ? text[..120] + "…" : text;
    }

    private sealed class Missing
    {
        internal static readonly Missing Value = new();
    }
}
