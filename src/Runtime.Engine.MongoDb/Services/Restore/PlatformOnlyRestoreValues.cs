using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore;

/// <summary>
///     The values a restore never brings into a tenant: the secret defaults the controller projects and the sealed caller
///     of a pipeline execution. The secret values of an Application's secret default declarations are the hosting
///     tenant's own input and restore like every tenant secret, because a redeploy of the Application needs them.
/// </summary>
internal static class PlatformOnlyRestoreValues
{
    /// <summary>Collections a restore leaves out entirely: the secret defaults the controller projects.</summary>
    public static IReadOnlyList<string> LeftOutCollections => MongoDumpOptions.PlatformOnlyTenantCollections;

    /// <summary>The attributes that a restore removes.</summary>
    public static IReadOnlyList<PlatformOnlyAttributeValue> AttributeValues { get; } =
    [
        new("System.Communication/PipelineExecution", "SealedCaller"),
        new("System.Communication/PipelineExecution", "CallerAccessToken")
    ];
}

/// <summary>An attribute of a CK type (and its derived types) that a restore removes.</summary>
internal sealed record PlatformOnlyAttributeValue(string CkTypeId, string Attribute)
{
    /// <summary>
    ///     The stored keys of the attribute in an entity's attributes document, matched ignoring case, because the
    ///     engine reads a stored key in any first-letter case.
    /// </summary>
    public IEnumerable<string> FindStoredKeys(BsonDocument attributes) =>
        attributes.Elements
            .Where(element => string.Equals(element.Name, Attribute, StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Name)
            .ToList();
}
