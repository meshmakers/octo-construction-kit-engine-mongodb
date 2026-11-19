using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore;

/// <summary>
///     Removes the <see cref="PlatformOnlyRestoreValues" /> from a restored tenant database before it is attached, so no
///     service reads them in between. Works on the stored documents of every entity collection, whatever the stored form
///     of a value: the CK type is matched with its derived types (read from the restored inheritance rows), and attribute
///     and record member keys are matched ignoring case, because the engine reads a stored key in any first-letter case.
/// </summary>
internal class RestoredPlatformOnlyValueRemover(
    IAdminRepositoryAccess adminRepositoryAccess,
    ILogger<RestoredPlatformOnlyValueRemover> logger)
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

        var typeFamilies = await RestoredCkTypeFamilies.ReadAsync(database, cancellationToken);
        var removals = PlatformOnlyRestoreValues.All
            .Select(platformOnlyValue => new TypedRemoval(platformOnlyValue,
                typeFamilies.WithDerivedTypes(platformOnlyValue.CkTypeId)))
            .ToList();
        var allCkTypeIds = removals.SelectMany(removal => removal.CkTypeIds).Distinct().ToList();

        var removedPaths = new List<string>();
        var collectionNames = await (await database.ListCollectionNamesAsync(cancellationToken: cancellationToken))
            .ToListAsync(cancellationToken);
        foreach (var collectionName in collectionNames.Where(name =>
                     name.StartsWith(EntityCollectionPrefix, StringComparison.Ordinal)))
        {
            var collection = database.GetCollection<BsonDocument>(collectionName);
            var candidates = await collection.Find(Builders<BsonDocument>.Filter.In(CkTypeIdField, allCkTypeIds))
                .Project(Builders<BsonDocument>.Projection.Include(CkTypeIdField).Include(AttributesField))
                .ToListAsync(cancellationToken);
            foreach (var candidate in candidates)
            {
                removedPaths.AddRange(await RemoveFromDocumentAsync(collection, candidate, removals,
                    cancellationToken));
            }
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

    private static async Task<IReadOnlyList<string>> RemoveFromDocumentAsync(IMongoCollection<BsonDocument> collection,
        BsonDocument candidate, IReadOnlyList<TypedRemoval> removals, CancellationToken cancellationToken)
    {
        var ckTypeId = candidate[CkTypeIdField].AsString;
        if (!candidate.TryGetValue(AttributesField, out var attributesValue) ||
            attributesValue is not BsonDocument attributes)
        {
            return [];
        }

        var removedPaths = new List<string>();
        var storedFieldPaths = new List<string>();
        foreach (var removal in removals.Where(removal => removal.CkTypeIds.Contains(ckTypeId)))
        {
            foreach (var (storedFieldPath, reportedPath) in removal.Value.FindStoredValues(attributes))
            {
                storedFieldPaths.Add($"{AttributesField}.{storedFieldPath}");
                removedPaths.Add($"{ckTypeId}@{candidate[IdField]}.{reportedPath}");
            }
        }

        if (storedFieldPaths.Count > 0)
        {
            var update = Builders<BsonDocument>.Update.Combine(
                storedFieldPaths.Select(fieldPath => Builders<BsonDocument>.Update.Unset(fieldPath)));
            await collection.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq(IdField, candidate[IdField]), update,
                cancellationToken: cancellationToken);
        }

        return removedPaths;
    }

    private IMongoDatabase GetDatabase(string databaseName)
    {
        var normalizedName = TenantDatabaseRegistrationLookup.Normalize(databaseName);
        var client = adminRepositoryAccess.GetRepositoryClient(normalizedName);
        return ((MongoRepository)client.GetRepository(normalizedName)).Database;
    }

    private sealed record TypedRemoval(IPlatformOnlyValue Value, IReadOnlySet<string> CkTypeIds);
}
