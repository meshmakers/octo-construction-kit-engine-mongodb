using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
/// Abstracts attribute metadata lookup for MongoDB field path resolution.
/// Implementations can be backed by the CK cache (for queries) or by
/// pre-fetched database entities (for index creation during import).
/// </summary>
internal interface IAttributeMetadataProvider
{
    /// <summary>
    /// Whether the current context is a record (nested record attributes
    /// require an additional ".attributes." segment in the MongoDB path).
    /// </summary>
    bool IsRecordContext { get; }

    /// <summary>
    /// Looks up an attribute by its PascalCase name and returns its value type.
    /// </summary>
    /// <param name="attributeName">The attribute name in PascalCase</param>
    /// <param name="valueType">The attribute's value type if found</param>
    /// <returns>True if the attribute was found</returns>
    bool TryGetAttribute(string attributeName, out AttributeValueTypesDto valueType);

    /// <summary>
    /// Navigates into a nested record attribute and returns a new provider
    /// scoped to that record's attributes.
    /// </summary>
    /// <param name="attributeName">The PascalCase name of the Record/RecordArray attribute</param>
    /// <returns>A new provider for the nested record, or null if navigation failed</returns>
    IAttributeMetadataProvider? NavigateToRecord(string attributeName);
}
