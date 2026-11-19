using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

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
}

/// <summary>An attribute of a record inside the record array attribute of a CK type.</summary>
internal sealed record PlatformOnlyRecordValue(string CkTypeId, string RecordArrayAttribute, string RecordAttribute);

/// <summary>An attribute of a CK type.</summary>
internal sealed record PlatformOnlyAttributeValue(string CkTypeId, string Attribute);
