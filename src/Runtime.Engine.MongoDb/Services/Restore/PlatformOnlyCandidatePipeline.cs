using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore;

/// <summary>
///     The aggregation that finds the restored entities of the given CK types that hold a platform-only attribute (any
///     first-letter case) or store their attributes in another form than a document. It returns the id, the CK type, the
///     stored form of the attributes and their names, never a value.
/// </summary>
internal static class PlatformOnlyCandidatePipeline
{
    public const string CkTypeIdField = "ckTypeId";
    public const string IdField = "_id";
    public const string AttributesTypeField = "attributesType";
    public const string AttributeNamesField = "attributeNames";

    private const string AttributesField = Constants.AttributesName;

    /// <summary>The stored forms of the attributes the removal can work on: a document, or none at all.</summary>
    public static readonly IReadOnlySet<string> ReadableAttributesTypes =
        new HashSet<string>(StringComparer.Ordinal) { "object", "missing", "null" };

    public static PipelineDefinition<BsonDocument, BsonDocument> Create(IEnumerable<string> ckTypeIds,
        IEnumerable<string> attributeNames)
    {
        var lowerCaseAttributeNames = new BsonArray(attributeNames.Select(name => name.ToLowerInvariant()).Distinct());
        var attributesType = new BsonDocument("$type", $"${AttributesField}");
        var storedAttributeNames = new BsonDocument("$map", new BsonDocument
        {
            { "input", new BsonDocument("$objectToArray", $"${AttributesField}") },
            { "as", "attribute" },
            { "in", "$$attribute.k" }
        });
        var nameIsPlatformOnly = new BsonDocument("$in", new BsonArray
        {
            new BsonDocument("$toLower", "$$name"), lowerCaseAttributeNames
        });
        var holdsAPlatformOnlyName = new BsonDocument("$anyElementTrue", new BsonArray
        {
            new BsonDocument("$map", new BsonDocument
            {
                { "input", $"${AttributeNamesField}" }, { "as", "name" }, { "in", nameIsPlatformOnly }
            })
        });
        var isUnreadableForm = new BsonDocument("$not", new BsonArray
        {
            new BsonDocument("$in", new BsonArray { $"${AttributesTypeField}", new BsonArray(ReadableAttributesTypes) })
        });
        var namesWhenDocument = new BsonDocument("$cond", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { attributesType, "object" }), storedAttributeNames, new BsonArray()
        });

        return PipelineDefinition<BsonDocument, BsonDocument>.Create(
            new BsonDocument("$match",
                new BsonDocument(CkTypeIdField, new BsonDocument("$in", new BsonArray(ckTypeIds.Distinct())))),
            new BsonDocument("$project", new BsonDocument
            {
                { CkTypeIdField, 1 }, { AttributesTypeField, attributesType }, { AttributeNamesField, namesWhenDocument }
            }),
            new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$or", new BsonArray
            {
                isUnreadableForm, holdsAPlatformOnlyName
            }))));
    }
}
