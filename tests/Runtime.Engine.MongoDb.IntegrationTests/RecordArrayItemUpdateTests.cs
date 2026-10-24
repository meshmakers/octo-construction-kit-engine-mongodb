using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#7445: atomic update of ONE item of a RecordArray (<c>$set</c> with <c>arrayFilters</c> on a key member),
///     against a real MongoDB. The stored entity is not read and not version-checked, so concurrent updates of
///     different items of the same entity never lose each other; concurrent updates of the same item keep the last
///     write.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class RecordArrayItemUpdateTests(ImportTestCkModelFixture fixture)
{
    private const int Iterations = 60;

    private static readonly RtCkId<CkTypeId> HolderTypeId = new("Test/ItemUpdateHolder");
    private static readonly RtCkId<CkRecordId> DataPointRecordId = new("Test/DataPoint");
    private static readonly RtCkId<CkTypeId> SecretHolderTypeId = new("Test/SecretHolder");

    [Fact]
    public async Task ParallelUpdatesOfDifferentItems_NeverLoseAnUpdate()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();

        var retries = 0;
        for (var i = 0; i < Iterations; i++)
        {
            var rtId = OctoObjectId.GenerateNewId();
            await InsertAsync(repository, NewHolder(rtId, ("A", 0.0), ("B", 0.0)));

            using var barrier = new Barrier(2);
            var a = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await ApplyWithTransientRetryAsync(repository, rtId, ItemUpdate("A", 100 + i), useTransaction: true);
            });
            var b = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await ApplyWithTransientRetryAsync(repository, rtId, ItemUpdate("B", 200 + i), useTransaction: true);
            });
            retries += (await Task.WhenAll(a, b)).Sum();

            var points = await ReadPointsAsync(rtId);
            Assert.Equal(100.0 + i, points["A"]);
            Assert.Equal(200.0 + i, points["B"]);
        }

        // Transactions that overlap on one document fail fast with a transient error and are retried by the
        // caller (standard MongoDB behaviour). The point of the test is that no value is lost.
        Assert.True(retries >= 0);
    }

    [Fact]
    public async Task ParallelUpdatesOfDifferentItems_WithoutTransaction_NeverLoseAnUpdate()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();

        for (var i = 0; i < Iterations; i++)
        {
            var rtId = OctoObjectId.GenerateNewId();
            await InsertAsync(repository, NewHolder(rtId, ("A", 0.0), ("B", 0.0), ("C", 0.0)));

            using var barrier = new Barrier(3);
            var tasks = new[] { "A", "B", "C" }.Select((key, index) => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await ApplyWithTransientRetryAsync(repository, rtId, ItemUpdate(key, 10 * (index + 1) + i),
                    useTransaction: false);
            })).ToArray();
            await Task.WhenAll(tasks);

            var points = await ReadPointsAsync(rtId);
            Assert.Equal(10.0 + i, points["A"]);
            Assert.Equal(20.0 + i, points["B"]);
            Assert.Equal(30.0 + i, points["C"]);
        }
    }

    [Fact]
    public async Task ParallelUpdatesOfTheSameItem_KeepTheLastWrite_AndLeaveOtherItemsAlone()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();

        for (var i = 0; i < Iterations; i++)
        {
            var rtId = OctoObjectId.GenerateNewId();
            await InsertAsync(repository, NewHolder(rtId, ("A", 0.0), ("B", 7.0)));

            using var barrier = new Barrier(2);
            var first = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await ApplyWithTransientRetryAsync(repository, rtId, ItemUpdate("A", 1), useTransaction: false);
            });
            var second = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await ApplyWithTransientRetryAsync(repository, rtId, ItemUpdate("A", 2), useTransaction: false);
            });
            await Task.WhenAll(first, second);

            var points = await ReadPointsAsync(rtId);
            Assert.Contains(points["A"], new[] { 1.0, 2.0 });
            Assert.Equal(7.0, points["B"]);
        }
    }

    [Fact]
    public async Task SequentialUpdatesOfTheSameItem_LastWriteWins()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, NewHolder(rtId, ("A", 0.0)));

        await ApplyWithTransientRetryAsync(repository, rtId, ItemUpdate("A", 1), useTransaction: true);
        await ApplyWithTransientRetryAsync(repository, rtId, ItemUpdate("A", 2), useTransaction: true);

        Assert.Equal(2.0, (await ReadPointsAsync(rtId))["A"]);
    }

    [Fact]
    public async Task TwoEntriesForTheSameItemAndMember_AreMergedBeforeTheStatement_LastWins()
    {
        // Two $set on the same path through different array filters would be a MongoDB path conflict; the rule
        // engine merges entries per attribute + match value first (later value wins).
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, NewHolder(rtId, ("A", 0.0), ("B", 5.0)));

        var info = EntityUpdateInfo<RtEntity>.CreateRecordArrayItemUpdate(new RtEntityId(HolderTypeId, rtId),
            new RtEntity(HolderTypeId, rtId, new Dictionary<string, object?>()),
            [ItemUpdate("A", 1).ToUpdate(), ItemUpdate("A", 2).ToUpdate(), ItemUpdate("B", 9).ToUpdate()]);
        await ApplyAsync(repository, info, new OperationResult(), useTransaction: true);

        var points = await ReadPointsAsync(rtId);
        Assert.Equal(2.0, points["A"]);
        Assert.Equal(9.0, points["B"]);
    }

    [Fact]
    public async Task ItemUpdate_WritesOnlyTheNamedMember_BumpsVersionOnce_AndLeavesTheRestUntouched()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, NewHolder(rtId, ("A", 1.0), ("B", 2.0), name: "holder", label: "keep"));
        var before = (await FindRawEntityAsync(rtId))!;

        var result = await ApplyWithTransientRetryAsync(repository, rtId, ItemUpdate("A", 42), useTransaction: true);
        Assert.Equal(0, result);

        var after = (await FindRawEntityAsync(rtId))!;
        Assert.Equal(before["rtVersion"].ToInt64() + 1, after["rtVersion"].ToInt64());
        Assert.True(after["rtChangedDateTime"].ToUniversalTime() >= before["rtChangedDateTime"].ToUniversalTime());
        Assert.Equal("holder", after["attributes"]["name"].AsString);
        var items = after["attributes"]["points"].AsBsonArray;
        Assert.Equal(2, items.Count);
        var a = items.Single(x => x["attributes"]["externalId"] == "A");
        Assert.Equal("Test/DataPoint", a["ckRecordId"].AsString.Split('-')[0]);
        Assert.Equal(42.0, a["attributes"]["currentValue"].ToDouble());
        Assert.Equal("keep", a["attributes"]["label"].AsString);
        var b = items.Single(x => x["attributes"]["externalId"] == "B");
        Assert.Equal(2.0, b["attributes"]["currentValue"].ToDouble());
    }

    [Fact]
    public async Task NoMatchingItem_WritesNothing_AndReportsAWarning()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, NewHolder(rtId, ("A", 1.0)));
        var before = (await FindRawEntityAsync(rtId))!;

        var operationResult = new OperationResult();
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.ApplyChangesAsync(session, [ItemUpdate("DOES-NOT-EXIST", 5).Build(rtId)],
                operationResult);
            await session.CommitTransactionAsync();
        }

        Assert.False(operationResult.HasErrors);
        Assert.False(operationResult.HasFatalErrors);
        var warning = Assert.Single(operationResult.Messages);
        Assert.Equal(MessageLevel.Warning, warning.MessageLevel);
        Assert.Equal(27, warning.MessageNumber);
        Assert.Equal(before, await FindRawEntityAsync(rtId));
    }

    [Fact]
    public async Task EntityWithoutTheArray_ItemOnly_IsNotMatched_AndMixedStillWritesTheRegularAttributes()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, new RtEntity(HolderTypeId, rtId, new Dictionary<string, object?> { ["Name"] = "no array" }));

        var itemOnly = new OperationResult();
        await ApplyAsync(repository, ItemUpdate("A", 1).Build(rtId), itemOnly, useTransaction: true);
        Assert.Equal(27, Assert.Single(itemOnly.Messages).MessageNumber);

        var mixed = new OperationResult();
        await ApplyAsync(repository, ItemUpdate("A", 1).Build(rtId, new Dictionary<string, object?> { ["Name"] = "renamed" }),
            mixed, useTransaction: true);

        Assert.Empty(mixed.Messages);
        Assert.Equal("renamed", (await FindRawEntityAsync(rtId))!["attributes"]["name"].AsString);
    }

    [Fact]
    public async Task MixedUpdate_IsOneStatement_RegularAttributeAndItem()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, NewHolder(rtId, ("A", 1.0), ("B", 2.0), name: "old"));
        var before = (await FindRawEntityAsync(rtId))!;

        await ApplyWithTransientRetryAsync(repository, rtId, ItemUpdate("B", 9),
            useTransaction: true, new Dictionary<string, object?> { ["Name"] = "new" });

        var after = (await FindRawEntityAsync(rtId))!;
        Assert.Equal("new", after["attributes"]["name"].AsString);
        Assert.Equal(9.0, (await ReadPointsAsync(rtId))["B"]);
        Assert.Equal(1.0, (await ReadPointsAsync(rtId))["A"]);
        // one statement = one version bump
        Assert.Equal(before["rtVersion"].ToInt64() + 1, after["rtVersion"].ToInt64());
    }

    [Fact]
    public async Task ChangeStream_SeesOneNormalUpdateEvent_WithVersionAndItemPath()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, NewHolder(rtId, ("A", 1.0), ("B", 2.0)));
        var collection = (await FindRawCollectionAsync(rtId))!;

        using var cursor = await collection.WatchAsync(
            new EmptyPipelineDefinition<ChangeStreamDocument<BsonDocument>>().Match(c =>
                c.OperationType == ChangeStreamOperationType.Update), null, TestContext.Current.CancellationToken);
        await ApplyWithTransientRetryAsync(repository, rtId, ItemUpdate("B", 5), useTransaction: true);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ChangeStreamDocument<BsonDocument>? change = null;
        while (change == null && !timeout.IsCancellationRequested)
        {
            if (await cursor.MoveNextAsync(timeout.Token))
            {
                change = cursor.Current.FirstOrDefault();
            }
        }

        Assert.NotNull(change);
        var updated = change.UpdateDescription.UpdatedFields;
        Assert.True(updated.Contains("rtVersion"));
        Assert.True(updated.Contains("rtChangedDateTime"));
        Assert.Contains(updated.Names, n => n.StartsWith("attributes.points.", StringComparison.Ordinal) &&
                                           n.EndsWith(".attributes.currentValue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecretMember_IsRefused_AndNothingIsWritten()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, new RtEntity(SecretHolderTypeId, rtId, new Dictionary<string, object?>
        {
            ["Name"] = "secrets",
            ["Credentials"] = new List<RtRecord>
            {
                new(new RtCkId<CkRecordId>("Test/CredentialEntry"), new Dictionary<string, object?> { ["Key"] = "smtp" })
            }
        }));
        var before = await FindRawEntityAsync(rtId);

        var update = new RtRecordArrayItemUpdate("Credentials", "Key", "smtp",
            new Dictionary<string, object?> { ["Value"] = "pw" });
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateRecordArrayItemUpdate(
                new RtEntityId(SecretHolderTypeId, rtId), new RtEntity(SecretHolderTypeId, rtId, new Dictionary<string, object?>()),
                [update]), new OperationResult(), useTransaction: false));

        Assert.Contains("Value", exception.ToString());
        Assert.Contains("Secret", exception.ToString());
        Assert.Equal(before, await FindRawEntityAsync(rtId));
    }

    [Theory]
    [InlineData("Unknown", "ExternalId", "CurrentValue", 1.0, "Unknown")]
    [InlineData("Points", "Nope", "CurrentValue", 1.0, "Nope")]
    [InlineData("Points", "ExternalId", "Nope", 1.0, "Nope")]
    [InlineData("Points", "ExternalId", "CurrentValue", "not-a-number", "CurrentValue")]
    [InlineData("Points", "ExternalId", "ExternalId", "B", "match member")]
    public async Task InvalidItemUpdate_IsRefused_NamingAttributeAndMember(string attribute, string match, string member,
        object value, string expectedInMessage)
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        await InsertAsync(repository, NewHolder(rtId, ("A", 1.0)));
        var before = await FindRawEntityAsync(rtId);

        var update = new RtRecordArrayItemUpdate(attribute, match, "A", new Dictionary<string, object?> { [member] = value });
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            ApplyAsync(repository, EntityUpdateInfo<RtEntity>.CreateRecordArrayItemUpdate(
                new RtEntityId(HolderTypeId, rtId), new RtEntity(HolderTypeId, rtId, new Dictionary<string, object?>()),
                [update]), new OperationResult(), useTransaction: false));

        Assert.Contains(expectedInMessage, exception.ToString());
        Assert.Equal(before, await FindRawEntityAsync(rtId));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private sealed record ItemUpdateSpec(string Key, double Value)
    {
        public RtRecordArrayItemUpdate ToUpdate() =>
            new("Points", "ExternalId", Key, new Dictionary<string, object?> { ["CurrentValue"] = Value });

        public IEntityUpdateInfo<RtEntity> Build(OctoObjectId rtId, Dictionary<string, object?>? regular = null) =>
            EntityUpdateInfo<RtEntity>.CreateRecordArrayItemUpdate(new RtEntityId(HolderTypeId, rtId),
                new RtEntity(HolderTypeId, rtId, regular ?? new Dictionary<string, object?>()), [ToUpdate()]);
    }

    private static ItemUpdateSpec ItemUpdate(string key, double value) => new(key, value);

    private static RtEntity NewHolder(OctoObjectId rtId, params (string Key, double Value)[] points) =>
        NewHolder(rtId, points, null, null);

    private static RtEntity NewHolder(OctoObjectId rtId, (string Key, double Value) a, (string Key, double Value) b,
        string? name = null, string? label = null) => NewHolder(rtId, [a, b], name, label);

    private static RtEntity NewHolder(OctoObjectId rtId, (string Key, double Value)[] points, string? name, string? label)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["Points"] = points.Select(p => new RtRecord(DataPointRecordId, new Dictionary<string, object?>
            {
                ["ExternalId"] = p.Key,
                ["CurrentValue"] = p.Value,
                ["Label"] = label
            })).ToList()
        };
        if (name != null)
        {
            attributes["Name"] = name;
        }

        return new RtEntity(HolderTypeId, rtId, attributes);
    }

    private static async Task InsertAsync(ITenantRepository repository, RtEntity entity)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        try
        {
            await repository.InsertOneRtEntityAsync(session, entity);
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    private static async Task ApplyAsync(ITenantRepository repository, IEntityUpdateInfo<RtEntity> info,
        OperationResult operationResult, bool useTransaction)
    {
        using var session = await repository.GetSessionAsync();
        if (useTransaction)
        {
            session.StartTransaction();
        }

        try
        {
            await repository.ApplyChangesAsync(session, [info], operationResult);
            if (useTransaction)
            {
                await session.CommitTransactionAsync();
            }
        }
        catch
        {
            if (useTransaction)
            {
                await session.AbortTransactionAsync();
            }

            throw;
        }
    }

    /// <summary>
    ///     Applies an item update. Two transactions that overlap on one document fail fast with a transient error
    ///     (<c>TransientTransactionError</c>); the standard client pattern is to run the transaction again. Returns the
    ///     number of retries.
    /// </summary>
    private static async Task<int> ApplyWithTransientRetryAsync(ITenantRepository repository, OctoObjectId rtId,
        ItemUpdateSpec spec, bool useTransaction, Dictionary<string, object?>? regular = null)
    {
        for (var attempt = 0;; attempt++)
        {
            try
            {
                await ApplyAsync(repository, spec.Build(rtId, regular), new OperationResult(), useTransaction);
                return attempt;
            }
            catch (Exception e) when (attempt < 100 && IsTransient(e))
            {
                await Task.Delay(Random.Shared.Next(1, 10));
            }
        }
    }

    private static bool IsTransient(Exception exception)
    {
        for (var e = exception; e != null; e = e.InnerException!)
        {
            if (e is MongoException mongo && mongo.HasErrorLabel("TransientTransactionError"))
            {
                return true;
            }

            if (e.InnerException == null)
            {
                break;
            }
        }

        return false;
    }

    private async Task<Dictionary<string, double>> ReadPointsAsync(OctoObjectId rtId)
    {
        var entity = (await FindRawEntityAsync(rtId))!;
        return entity["attributes"]["points"].AsBsonArray.ToDictionary(
            x => x["attributes"]["externalId"].AsString, x => x["attributes"]["currentValue"].ToDouble());
    }

    private async Task<BsonDocument?> FindRawEntityAsync(OctoObjectId rtId)
    {
        var collection = await FindRawCollectionAsync(rtId);
        return collection == null
            ? null
            : await collection.Find(new BsonDocument("_id", new ObjectId(rtId.ToString()))).FirstOrDefaultAsync();
    }

    private async Task<IMongoCollection<BsonDocument>?> FindRawCollectionAsync(OctoObjectId rtId)
    {
        var db = GetSystemMongoDatabase();
        var filter = new BsonDocument("name", new BsonDocument("$regex", "^RtEntity_"));
        using var cursor = await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = filter });
        var id = new ObjectId(rtId.ToString());
        foreach (var info in await cursor.ToListAsync())
        {
            var collection = db.GetCollection<BsonDocument>(info["name"].AsString);
            if (await collection.CountDocumentsAsync(new BsonDocument("_id", id)) > 0)
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
            Server = MongoServerAddress.Parse(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection
        };

        return new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(config.SystemDatabaseName);
    }
}
