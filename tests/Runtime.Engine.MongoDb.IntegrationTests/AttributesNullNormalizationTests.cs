using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#5148: an entity persisted without attributes must carry <c>attributes: {}</c>, never an
///     explicit <c>attributes: null</c> — MongoDB cannot create a field beneath a null value, so a
///     null poisons the entity for every later partial update that $sets an
///     <c>attributes.&lt;name&gt;</c> subpath (write error code 28). Covers both layers of the fix:
///     the write side (<c>RtAttributeDictionarySerializer</c> serializes an empty attribute
///     dictionary as an empty document) and the update side (the engine heals a stored null before
///     applying attribute subpath updates, so existing poisoned documents recover on first write).
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class AttributesNullNormalizationTests(ImportTestCkModelFixture fixture)
{
    [Fact]
    public async Task Insert_WithoutAttributes_PersistsEmptyAttributesDocument()
    {
        await fixture.ClearCollectionAsync();
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        var rtId = OctoObjectId.GenerateNewId();
        await InsertWatchTargetWithoutAttributesAsync(tenantRepository, rtId);

        var rawDocument = await FindRawEntityDocumentAsync(rtId);
        Assert.NotNull(rawDocument);
        Assert.True(rawDocument!.Contains("attributes"), "the attributes field must be persisted");
        Assert.True(rawDocument["attributes"].IsBsonDocument,
            $"attributes must be persisted as a document, but was {rawDocument["attributes"].BsonType}");
        Assert.Empty(rawDocument["attributes"].AsBsonDocument);
    }

    [Fact]
    public async Task Update_AfterInsertWithoutAttributes_SetsAttribute()
    {
        // The end-to-end repro from the work item: import/create an entity with no attributes,
        // then update a single attribute. Before the fix the insert persisted `attributes: null`
        // and the update failed with MongoWriteException code 28.
        await fixture.ClearCollectionAsync();
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        var rtId = OctoObjectId.GenerateNewId();
        await InsertWatchTargetWithoutAttributesAsync(tenantRepository, rtId);

        await UpdateWatchTargetNameAsync(tenantRepository, rtId, "Updated");

        var loaded = await LoadSingleWatchTargetAsync(tenantRepository);
        Assert.Equal("Updated", loaded.Name);
    }

    [Fact]
    public async Task Update_OnPoisonedNullAttributesDocument_HealsAndApplies()
    {
        // Defense in depth: documents written before the serializer fix still carry
        // `attributes: null`. The first attribute update must repair the document instead of
        // erroring with Mongo code 28.
        await fixture.ClearCollectionAsync();
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        var rtId = OctoObjectId.GenerateNewId();
        await InsertWatchTargetWithoutAttributesAsync(tenantRepository, rtId);
        await PoisonAttributesToNullAsync(rtId);

        await UpdateWatchTargetNameAsync(tenantRepository, rtId, "Healed");

        var loaded = await LoadSingleWatchTargetAsync(tenantRepository);
        Assert.Equal("Healed", loaded.Name);

        var rawDocument = await FindRawEntityDocumentAsync(rtId);
        Assert.NotNull(rawDocument);
        Assert.True(rawDocument!["attributes"].IsBsonDocument,
            "the stored null must be normalized to a document by the first attribute update");
    }

    [Fact]
    public async Task ConditionalUpdate_OnPoisonedNullAttributesDocument_HealsAndApplies()
    {
        // Same heal through the guarded (optimistic-concurrency) update path.
        await fixture.ClearCollectionAsync();
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        var rtId = OctoObjectId.GenerateNewId();
        var rtEntityId = new RtEntityId(TestCkIds.RtCkWatchTargetTypeId, rtId);
        await InsertWatchTargetWithoutAttributesAsync(tenantRepository, rtId);
        await PoisonAttributesToNullAsync(rtId);

        var guard = new AttributeNewerThanGuard("rtChangedDateTime", DateTime.UtcNow.AddHours(1));
        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            var operationResult = new OperationResult();
            var update = new RtEntity(TestCkIds.RtCkWatchTargetTypeId, rtId,
                new Dictionary<string, object?> { { "Name", "Healed-Conditional" } });
            var entityUpdates = new List<IEntityUpdateInfo<RtEntity>>
            {
                EntityUpdateInfo<RtEntity>.CreateConditionalUpdate(rtEntityId, update, guard)
            };
            await tenantRepository.ApplyChangesAsync(session, entityUpdates, operationResult);
            Assert.False(operationResult.HasErrors);
            await session.CommitTransactionAsync();
        }

        var loaded = await LoadSingleWatchTargetAsync(tenantRepository);
        Assert.Equal("Healed-Conditional", loaded.Name);
    }

    private static async Task InsertWatchTargetWithoutAttributesAsync(ITenantRepository tenantRepository,
        OctoObjectId rtId)
    {
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var rtWatchTarget = await tenantRepository.CreateTransientRtEntityAsync<RtWatchTarget>();
        rtWatchTarget.RtId = rtId;
        Assert.Empty(rtWatchTarget.Attributes); // the repro requires a truly attribute-less entity
        await tenantRepository.InsertOneRtEntityAsync(session, rtWatchTarget);
        await session.CommitTransactionAsync();
    }

    private static async Task UpdateWatchTargetNameAsync(ITenantRepository tenantRepository, OctoObjectId rtId,
        string name)
    {
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var operationResult = new OperationResult();
        var update = new RtEntity(TestCkIds.RtCkWatchTargetTypeId, rtId,
            new Dictionary<string, object?> { { "Name", name } });
        var entityUpdates = new List<IEntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(TestCkIds.RtCkWatchTargetTypeId, rtId), update)
        };
        await tenantRepository.ApplyChangesAsync(session, entityUpdates, operationResult);
        Assert.False(operationResult.HasErrors);
        await session.CommitTransactionAsync();
    }

    private static async Task<RtWatchTarget> LoadSingleWatchTargetAsync(ITenantRepository tenantRepository)
    {
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var loaded = await tenantRepository.GetRtEntitiesByTypeAsync<RtWatchTarget>(session,
            RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();
        return Assert.Single(loaded.Items);
    }

    /// <summary>
    ///     Reproduces the pre-fix on-disk state: rewrites the stored attributes document to an
    ///     explicit BSON null, exactly what the serializer used to write for an attribute-less
    ///     entity.
    /// </summary>
    private async Task PoisonAttributesToNullAsync(OctoObjectId rtId)
    {
        var collection = await FindRawEntityCollectionAsync(rtId);
        Assert.NotNull(collection);
        var result = await collection!.UpdateOneAsync(
            new BsonDocument("_id", new ObjectId(rtId.ToString())),
            new BsonDocument("$set", new BsonDocument("attributes", BsonNull.Value)));
        Assert.Equal(1, result.ModifiedCount);
    }

    private async Task<BsonDocument?> FindRawEntityDocumentAsync(OctoObjectId rtId)
    {
        var collection = await FindRawEntityCollectionAsync(rtId);
        if (collection == null)
        {
            return null;
        }

        return await collection.Find(new BsonDocument("_id", new ObjectId(rtId.ToString())))
            .FirstOrDefaultAsync();
    }

    private async Task<IMongoCollection<BsonDocument>?> FindRawEntityCollectionAsync(OctoObjectId rtId)
    {
        var db = GetSystemMongoDatabase();
        var filter = new BsonDocument("name", new BsonDocument("$regex", "^RtEntity_"));
        using var cursor = await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = filter });
        var infos = await cursor.ToListAsync();

        var id = new ObjectId(rtId.ToString());
        foreach (var info in infos)
        {
            var collection = db.GetCollection<BsonDocument>(info["name"].AsString);
            var count = await collection.CountDocumentsAsync(new BsonDocument("_id", id));
            if (count > 0)
            {
                return collection;
            }
        }

        return null;
    }

    private IMongoDatabase GetSystemMongoDatabase()
    {
        var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;

        var urlBuilder = new MongoUrlBuilder
        {
            // Parse, not the string ctor: DatabaseHost is "host:port", which MongoDB.Driver >= 3.11.1
            // rejects in the ctor (CSHARP-6171) — same fix as in the production repository clients.
            Server = MongoServerAddress.Parse(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection
        };

        var client = new MongoClient(urlBuilder.ToMongoUrl());
        return client.GetDatabase(config.SystemDatabaseName);
    }
}
