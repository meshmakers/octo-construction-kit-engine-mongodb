using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore;

/// <summary>
///     Removes the <see cref="PlatformOnlyRestoreValues" /> from a restored tenant database before it is attached, so no
///     service reads them in between. Works on the stored documents (field <c>ckTypeId</c>, attributes in camel case
///     under <c>attributes</c>), whatever form the stored value has.
/// </summary>
internal class RestoredPlatformOnlyValueRemover(IAdminRepositoryAccess adminRepositoryAccess)
{
    private const string EntityCollectionPrefix = "RtEntity_";
    private const string CkTypeIdField = "ckTypeId";
    private const string IdField = "_id";
    private const string AttributesField = Constants.AttributesName;

    /// <summary>Removes the values and returns their paths (CK type, rtId, attribute path), never the values.</summary>
    public virtual async Task<IReadOnlyList<string>> RemoveAsync(string databaseName,
        CancellationToken cancellationToken)
    {
        var database = GetDatabase(databaseName);
        foreach (var leftOutCollection in PlatformOnlyRestoreValues.LeftOutCollections)
        {
            await database.DropCollectionAsync(leftOutCollection, cancellationToken);
        }

        var removedPaths = new List<string>();
        var collectionNames = await (await database.ListCollectionNamesAsync(cancellationToken: cancellationToken))
            .ToListAsync(cancellationToken);
        foreach (var collectionName in collectionNames.Where(name =>
                     name.StartsWith(EntityCollectionPrefix, StringComparison.Ordinal)))
        {
            var collection = database.GetCollection<BsonDocument>(collectionName);
            foreach (var recordValue in PlatformOnlyRestoreValues.RecordValues)
            {
                removedPaths.AddRange(await RemoveRecordValuesAsync(collection, recordValue, cancellationToken));
            }

            foreach (var attributeValue in PlatformOnlyRestoreValues.AttributeValues)
            {
                removedPaths.AddRange(await RemoveAttributeValuesAsync(collection, attributeValue, cancellationToken));
            }
        }

        return removedPaths;
    }

    private static async Task<IReadOnlyList<string>> RemoveRecordValuesAsync(IMongoCollection<BsonDocument> collection,
        PlatformOnlyRecordValue recordValue, CancellationToken cancellationToken)
    {
        var arrayField = $"{AttributesField}.{recordValue.RecordArrayAttribute.ToCamelCase()}";
        var recordField = recordValue.RecordAttribute.ToCamelCase();
        var valueInRecordField = $"{arrayField}.{AttributesField}.{recordField}";
        var filter = Builders<BsonDocument>.Filter.Eq(CkTypeIdField, recordValue.CkTypeId) &
                     Builders<BsonDocument>.Filter.Exists(valueInRecordField);
        var documents = await collection.Find(filter)
            .Project(Builders<BsonDocument>.Projection.Include(arrayField)).ToListAsync(cancellationToken);

        var removedPaths = new List<string>();
        foreach (var document in documents)
        {
            var records = ReadArray(document, arrayField);
            for (var recordIndex = 0; recordIndex < records.Count; recordIndex++)
            {
                if (records[recordIndex] is BsonDocument record &&
                    record.TryGetValue(AttributesField, out var recordAttributes) &&
                    recordAttributes is BsonDocument recordAttributeDocument &&
                    recordAttributeDocument.Contains(recordField))
                {
                    removedPaths.Add(
                        $"{recordValue.CkTypeId}@{document[IdField]}.{recordValue.RecordArrayAttribute}[{recordIndex}].{recordValue.RecordAttribute}");
                }
            }
        }

        if (documents.Count > 0)
        {
            var update = Builders<BsonDocument>.Update.Unset($"{arrayField}.$[].{AttributesField}.{recordField}");
            await collection.UpdateManyAsync(filter, update, cancellationToken: cancellationToken);
        }

        return removedPaths;
    }

    private static async Task<IReadOnlyList<string>> RemoveAttributeValuesAsync(
        IMongoCollection<BsonDocument> collection, PlatformOnlyAttributeValue attributeValue,
        CancellationToken cancellationToken)
    {
        var valueField = $"{AttributesField}.{attributeValue.Attribute.ToCamelCase()}";
        var filter = Builders<BsonDocument>.Filter.Eq(CkTypeIdField, attributeValue.CkTypeId) &
                     Builders<BsonDocument>.Filter.Exists(valueField);
        var documentIds = await collection.Find(filter).Project(Builders<BsonDocument>.Projection.Include(IdField))
            .ToListAsync(cancellationToken);
        if (documentIds.Count == 0)
        {
            return [];
        }

        await collection.UpdateManyAsync(filter, Builders<BsonDocument>.Update.Unset(valueField),
            cancellationToken: cancellationToken);
        return documentIds
            .Select(document => $"{attributeValue.CkTypeId}@{document[IdField]}.{attributeValue.Attribute}")
            .ToList();
    }

    private static BsonArray ReadArray(BsonDocument document, string dottedField)
    {
        BsonValue current = document;
        foreach (var fieldName in dottedField.Split('.'))
        {
            if (current is not BsonDocument currentDocument || !currentDocument.TryGetValue(fieldName, out current))
            {
                return [];
            }
        }

        return current as BsonArray ?? [];
    }

    private IMongoDatabase GetDatabase(string databaseName)
    {
        var normalizedName = TenantDatabaseRegistrationLookup.Normalize(databaseName);
        var client = adminRepositoryAccess.GetRepositoryClient(normalizedName);
        return ((MongoRepository)client.GetRepository(normalizedName)).Database;
    }
}
