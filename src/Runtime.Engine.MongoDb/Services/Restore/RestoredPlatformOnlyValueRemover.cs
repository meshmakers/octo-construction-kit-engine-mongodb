using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using static Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore.PlatformOnlyCandidatePipeline;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore;

/// <summary>
///     Removes the <see cref="PlatformOnlyRestoreValues" /> from a restored tenant database before it is attached, so no
///     service reads them in between: the left-out collections, every entity of a left-out CK type (or a type derived
///     from it) in any entity collection, and the platform-only attributes. CK types are matched with their derived types
///     (read from the restored inheritance rows), and attribute keys ignoring case, because the engine reads a stored key
///     in any first-letter case. A candidate whose attributes are stored in another form than a document (the engine also
///     reads an array of key-value pairs) fails the removal, so the restore fails closed.
/// </summary>
internal class RestoredPlatformOnlyValueRemover(
    IAdminRepositoryAccess adminRepositoryAccess,
    ILogger<RestoredPlatformOnlyValueRemover> logger)
{
    private const string EntityCollectionPrefix = "RtEntity_";
    private const string AttributesField = Constants.AttributesName;
    private const int UpdateBatchSize = 500;

    /// <summary>Removes the values and returns their paths (CK type, rtId, attribute path), never the values.</summary>
    public virtual async Task<IReadOnlyList<string>> RemoveAsync(string databaseName,
        CancellationToken cancellationToken)
    {
        var database = GetDatabase(databaseName);
        foreach (var leftOutCollection in PlatformOnlyRestoreValues.LeftOutCollections)
        {
            await database.DropCollectionAsync(leftOutCollection, cancellationToken);
        }

        var typeFamilies = await RestoredCkTypeFamilies.ReadAsync(database, cancellationToken);
        var leftOutCkTypeIds = PlatformOnlyRestoreValues.LeftOutCkTypeIds
            .SelectMany(typeFamilies.WithDerivedTypes).ToHashSet(StringComparer.Ordinal);
        var removals = PlatformOnlyRestoreValues.AttributeValues
            .Select(platformOnlyValue => new TypedRemoval(platformOnlyValue,
                typeFamilies.WithDerivedTypes(platformOnlyValue.CkTypeId)))
            .ToList();
        var candidatePipeline = Create(removals.SelectMany(removal => removal.CkTypeIds),
            removals.Select(removal => removal.Value.Attribute));

        var removedPaths = new List<string>();
        var collectionNames = await (await database.ListCollectionNamesAsync(cancellationToken: cancellationToken))
            .ToListAsync(cancellationToken);
        foreach (var collectionName in collectionNames.Where(name =>
                     name.StartsWith(EntityCollectionPrefix, StringComparison.Ordinal)))
        {
            var collection = database.GetCollection<BsonDocument>(collectionName);
            removedPaths.AddRange(await RemoveLeftOutEntitiesAsync(collection, leftOutCkTypeIds, cancellationToken));
            removedPaths.AddRange(await RemoveAttributesAsync(collection, candidatePipeline, removals,
                cancellationToken));
        }

        return removedPaths;
    }

    /// <summary>Drops a restored database whose platform-only values could not be removed; never throws.</summary>
    public virtual async Task DropRestoredDatabaseAsync(string databaseName)
    {
        try
        {
            var database = GetDatabase(databaseName);
            await database.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "The restored database '{DatabaseName}' could not be dropped after the platform-only values could not be removed; drop it by hand before anyone attaches it",
                databaseName);
        }
    }

    private static async Task<IReadOnlyList<string>> RemoveLeftOutEntitiesAsync(
        IMongoCollection<BsonDocument> collection, IReadOnlySet<string> leftOutCkTypeIds,
        CancellationToken cancellationToken)
    {
        var leftOutFilter = Builders<BsonDocument>.Filter.In(CkTypeIdField, leftOutCkTypeIds);
        var leftOutEntities = await collection.Find(leftOutFilter)
            .Project(Builders<BsonDocument>.Projection.Include(CkTypeIdField))
            .ToListAsync(cancellationToken);
        if (leftOutEntities.Count == 0)
        {
            return [];
        }

        await collection.DeleteManyAsync(leftOutFilter, cancellationToken);
        return leftOutEntities.Select(entity => $"{entity[CkTypeIdField]}@{entity[IdField]}").ToList();
    }

    private static async Task<IReadOnlyList<string>> RemoveAttributesAsync(IMongoCollection<BsonDocument> collection,
        PipelineDefinition<BsonDocument, BsonDocument> candidatePipeline, IReadOnlyList<TypedRemoval> removals,
        CancellationToken cancellationToken)
    {
        var removedPaths = new List<string>();
        var pendingUpdates = new List<WriteModel<BsonDocument>>();
        using var candidates = await collection.AggregateAsync(candidatePipeline, cancellationToken: cancellationToken);
        while (await candidates.MoveNextAsync(cancellationToken))
        {
            foreach (var candidate in candidates.Current)
            {
                var storedFieldPaths = StoredFieldPaths(collection, candidate, removals, removedPaths);
                if (storedFieldPaths.Count > 0)
                {
                    pendingUpdates.Add(new UpdateOneModel<BsonDocument>(
                        Builders<BsonDocument>.Filter.Eq(IdField, candidate[IdField]),
                        Builders<BsonDocument>.Update.Combine(storedFieldPaths
                            .Select(fieldPath => Builders<BsonDocument>.Update.Unset(fieldPath)))));
                }
            }

            if (pendingUpdates.Count >= UpdateBatchSize)
            {
                await collection.BulkWriteAsync(pendingUpdates, cancellationToken: cancellationToken);
                pendingUpdates.Clear();
            }
        }

        if (pendingUpdates.Count > 0)
        {
            await collection.BulkWriteAsync(pendingUpdates, cancellationToken: cancellationToken);
        }

        return removedPaths;
    }

    private static List<string> StoredFieldPaths(IMongoCollection<BsonDocument> collection, BsonDocument candidate,
        IReadOnlyList<TypedRemoval> removals, List<string> removedPaths)
    {
        var ckTypeId = candidate[CkTypeIdField].AsString;
        var storedForm = candidate[AttributesTypeField].AsString;
        if (!ReadableAttributesTypes.Contains(storedForm))
        {
            throw new InvalidOperationException(
                $"The restored entity {ckTypeId}@{candidate[IdField]} in '{collection.CollectionNamespace.CollectionName}' " +
                $"stores its attributes as '{storedForm}' instead of a document; its platform-only values cannot be removed safely.");
        }

        var storedAttributeNames = candidate[AttributeNamesField].AsBsonArray.Select(name => name.AsString).ToList();
        var storedFieldPaths = new List<string>();
        foreach (var removal in removals.Where(removal => removal.CkTypeIds.Contains(ckTypeId)))
        {
            foreach (var storedName in removal.Value.FindStoredKeys(storedAttributeNames))
            {
                storedFieldPaths.Add($"{AttributesField}.{storedName}");
                removedPaths.Add($"{ckTypeId}@{candidate[IdField]}.{removal.Value.Attribute}");
            }
        }

        return storedFieldPaths;
    }

    private IMongoDatabase GetDatabase(string databaseName)
    {
        var client = adminRepositoryAccess.GetRepositoryClient(databaseName);
        return ((MongoRepository)client.GetRepository(databaseName)).Database;
    }

    private sealed record TypedRemoval(PlatformOnlyAttributeValue Value, IReadOnlySet<string> CkTypeIds);
}
