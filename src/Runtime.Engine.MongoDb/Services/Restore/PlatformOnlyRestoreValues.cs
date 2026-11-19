using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore;

/// <summary>
///     The values a restore never brings into a tenant: what only the platform may hold or produce. Mirrors
///     <c>PlatformOnlySecrets</c> of the mesh adapter SDK (the values RevealSecret refuses to tenant identities) and adds
///     the sealed caller of a pipeline execution.
/// </summary>
internal static class PlatformOnlyRestoreValues
{
    /// <summary>Collections a restore leaves out entirely: the secret defaults the controller projects.</summary>
    public static IReadOnlyList<string> LeftOutCollections => MongoDumpOptions.PlatformOnlyTenantCollections;

    /// <summary>The record attributes inside a record array that a restore removes.</summary>
    public static IReadOnlyList<PlatformOnlyRecordValue> RecordValues { get; } =
    [
        new("System.Communication/Application", "SecretDefaults", "SecretValue")
    ];

    /// <summary>The attributes that a restore removes.</summary>
    public static IReadOnlyList<PlatformOnlyAttributeValue> AttributeValues { get; } =
    [
        new("System.Communication/PipelineExecution", "SealedCaller"),
        new("System.Communication/PipelineExecution", "CallerAccessToken")
    ];

    /// <summary>Every removed value, record members and attributes.</summary>
    public static IReadOnlyList<IPlatformOnlyValue> All { get; } = [.. RecordValues, .. AttributeValues];
}

/// <summary>A value of a CK type (and its derived types) that a restore removes.</summary>
internal interface IPlatformOnlyValue
{
    /// <summary>The CK type, <c>Model/Type</c>.</summary>
    string CkTypeId { get; }

    /// <summary>
    ///     The stored places of the value in an entity's attributes document: the field path relative to it and the path
    ///     reported to the operator. Keys are matched ignoring case.
    /// </summary>
    IEnumerable<(string StoredFieldPath, string ReportedPath)> FindStoredValues(BsonDocument attributes);
}

/// <summary>An attribute of a record inside the record array attribute of a CK type.</summary>
internal sealed record PlatformOnlyRecordValue(string CkTypeId, string RecordArrayAttribute, string RecordAttribute)
    : IPlatformOnlyValue
{
    private const string RecordAttributesField = Constants.AttributesName;

    /// <inheritdoc />
    public IEnumerable<(string StoredFieldPath, string ReportedPath)> FindStoredValues(BsonDocument attributes)
    {
        foreach (var arrayElement in StoredKeys.Matching(attributes, RecordArrayAttribute))
        {
            if (arrayElement.Value is not BsonArray records)
            {
                continue;
            }

            for (var recordIndex = 0; recordIndex < records.Count; recordIndex++)
            {
                if (records[recordIndex] is not BsonDocument record ||
                    !record.TryGetValue(RecordAttributesField, out var recordAttributesValue) ||
                    recordAttributesValue is not BsonDocument recordAttributes)
                {
                    continue;
                }

                foreach (var memberElement in StoredKeys.Matching(recordAttributes, RecordAttribute))
                {
                    yield return ($"{arrayElement.Name}.{recordIndex}.{RecordAttributesField}.{memberElement.Name}",
                        $"{RecordArrayAttribute}[{recordIndex}].{RecordAttribute}");
                }
            }
        }
    }
}

/// <summary>An attribute of a CK type.</summary>
internal sealed record PlatformOnlyAttributeValue(string CkTypeId, string Attribute) : IPlatformOnlyValue
{
    /// <inheritdoc />
    public IEnumerable<(string StoredFieldPath, string ReportedPath)> FindStoredValues(BsonDocument attributes) =>
        StoredKeys.Matching(attributes, Attribute).Select(element => (element.Name, Attribute));
}

internal static class StoredKeys
{
    /// <summary>The elements of <paramref name="document" /> whose key equals <paramref name="attributeName" /> ignoring case.</summary>
    public static IReadOnlyList<BsonElement> Matching(BsonDocument document, string attributeName) =>
        document.Elements
            .Where(element => string.Equals(element.Name, attributeName, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
