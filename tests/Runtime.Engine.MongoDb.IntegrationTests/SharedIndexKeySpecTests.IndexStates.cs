using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

using Microsoft.Extensions.Logging.Abstractions;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#9391: the stored index states of the types of a collection name exactly the indexes that exist, a failure stays
///     visible until the index is in its target shape, a failure never replaces the <c>Applied</c> state of an index that
///     exists, and an unchanged run writes no CkType document. Every test runs with both declaration orders.
/// </summary>
public partial class SharedIndexKeySpecTests
{
    /// <summary>MongoDB holds at most 64 indexes per collection (<c>_id_</c> included): one more build fails.</summary>
    private const int MaxIndexesPerCollection = 64;

    /// <summary>
    ///     AC 1: two types share one legacy index (both <c>Applied</c> under it after an update without the upgrade); the
    ///     upgrade builds one tiebreak index and drops the legacy one. No type keeps a state under the dropped name, each
    ///     is <c>Applied</c> under the index that serves it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeDropsTheSharedLegacyIndex_NoTypeKeepsAStateUnderItsName(bool betaFirst)
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("DropLeg", "1.0.0", betaFirst));
            var collection = Collection(tenant, "DropLeg");
            var legacyName = IndexBaseName("DropLeg", AlphaTypeName);
            await ReplaceFieldIndexesAsync(collection, (legacyName, LegacyKeys));
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpdateIndexesAsync(adminSession);
            }

            await AssertAppliedUnderAsync(tenant, "DropLeg-1.0.0", legacyName);

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
            }

            var shared = Assert.Single(await IndexesAsync(collection), i => KeysOf(i).Contains(FieldPath));
            Assert.Equal(TiebreakKeys, KeysOf(shared));
            await AssertAppliedUnderAsync(tenant, "DropLeg-1.0.0", shared["name"].AsString);
            await AssertNoStateUnderAsync(tenant, "DropLeg-1.0.0", legacyName);
        });
    }

    /// <summary>
    ///     AC 2: an upgrade that was aborted after the tiebreak index was built but before the states were persisted
    ///     (both indexes present, the only stored state of each type is <c>Applied</c> under the legacy name). The retry
    ///     drops the legacy index and its states, and each type is <c>Applied</c> under the tiebreak index the run found.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAfterAnAbortedUpgrade_DropsTheLegacyIndex_AndItsStates(bool betaFirst)
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("Retry", "1.0.0", betaFirst));
            var collection = Collection(tenant, "Retry");
            var legacyName = IndexBaseName("Retry", AlphaTypeName);
            var tiebreakName = legacyName + TiebreakIndexShape.NameSuffix;
            await ReplaceFieldIndexesAsync(collection, (legacyName, LegacyKeys), (tiebreakName, TiebreakKeys));
            foreach (var typeName in new[] { AlphaTypeName, BetaTypeName })
            {
                await AddStoredStateAsync(tenant, "Retry", typeName, legacyName, IndexState.Applied, onlyState: true);
            }

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
            }

            var indexes = await IndexesAsync(collection);
            Assert.DoesNotContain(indexes, i => i["name"].AsString == legacyName);
            Assert.Single(indexes, i => KeysOf(i).Contains(FieldPath));
            await AssertNoStateUnderAsync(tenant, "Retry-1.0.0", legacyName);
            await AssertAppliedUnderAsync(tenant, "Retry-1.0.0", tiebreakName);
        });
    }

    /// <summary>
    ///     AC 3: the tiebreak build of the upgrade fails (the collection holds 64 indexes), the shared legacy index is
    ///     kept and both types record <c>Failed</c> under their tiebreak index name and <c>Applied</c> under the legacy
    ///     index that still serves them (no state stored before the upgrade). A normal index update keeps the legacy index
    ///     and the failures; only a later successful upgrade clears them.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedTiebreakBuild_StaysVisibleThroughANormalUpdate_UntilTheUpgradeSucceeds(bool betaFirst)
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("FailUp", "1.0.0", betaFirst));
            var collection = Collection(tenant, "FailUp");
            var legacyName = IndexBaseName("FailUp", AlphaTypeName);
            await ReplaceFieldIndexesAsync(collection, (legacyName, LegacyKeys));
            await FillUpIndexesAsync(collection);
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
            }

            // The build failed: the legacy index serves both types, each has its failure.
            Assert.Contains(await IndexesAsync(collection), i => i["name"].AsString == legacyName);
            await AssertFailedUnderOwnTiebreakNameAsync(tenant, "FailUp");
            foreach (var ckType in await TypesAsync(tenant, "FailUp-1.0.0"))
            {
                Assert.Contains(ckType.IndexStates ?? [], s => s.State == IndexState.Applied && s.Name == legacyName);
            }

            // The cleanup of the failed run dropped the filler indexes; a normal update builds nothing and keeps the
            // failures (the indexes are still not in their target shape).
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpdateIndexesAsync(adminSession);
            }

            Assert.Contains(await IndexesAsync(collection), i => i["name"].AsString == legacyName);
            await AssertFailedUnderOwnTiebreakNameAsync(tenant, "FailUp");
            foreach (var ckType in await TypesAsync(tenant, "FailUp-1.0.0"))
            {
                Assert.Contains(ckType.IndexStates ?? [], s => s.State == IndexState.Applied && s.Name == legacyName);
            }

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
            }

            var shared = Assert.Single(await IndexesAsync(collection), i => KeysOf(i).Contains(FieldPath));
            Assert.Equal(TiebreakKeys, KeysOf(shared));
            await AssertAppliedUnderAsync(tenant, "FailUp-1.0.0", shared["name"].AsString);
            await AssertNoStateUnderAsync(tenant, "FailUp-1.0.0", legacyName);
        });
    }

    /// <summary>
    ///     AC 4: two definitions of one type; slot 0 adopts the existing index named after slot 1 (it has slot 0's keys),
    ///     slot 1's own build fails (the collection holds 64 indexes). The index that exists keeps <c>Applied</c>; slot
    ///     1's failure is recorded under the alternate name its index gets while the expected name is taken. The next
    ///     update builds it there and the failure is gone.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedBuildOfASlot_NeverReplacesTheAppliedStateOfTheIndexUnderItsName(bool otherFirst)
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(SwapModel("SwapFail", "1.0.0", otherFirst));
            var collection = Collection(tenant, "SwapFail");
            var slot1 = IndexBaseName("SwapFail", RootTypeName, 1) + TiebreakIndexShape.NameSuffix;
            string[] slot0Keys = ["ckTypeId", otherFirst ? OtherFieldPath : FieldPath, "_id", "rtState"];
            await ReplaceIndexesAsync(collection, [FieldPath, OtherFieldPath], (slot1, slot0Keys));
            await FillUpIndexesAsync(collection);

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpdateIndexesAsync(adminSession);
            }

            Assert.Contains(await IndexesAsync(collection),
                i => i["name"].AsString == slot1 && KeysOf(i).SequenceEqual(slot0Keys));
            var states = await RootStatesAsync(tenant, "SwapFail-1.0.0");
            Assert.Contains(states, s => s.State == IndexState.Applied && s.Name == slot1);
            var failed = Assert.Single(states, s => s.State == IndexState.Failed);
            Assert.NotEqual(slot1, failed.Name);
            Assert.StartsWith(slot1 + "_", failed.Name);
            Assert.False(string.IsNullOrEmpty(failed.ErrorMessage));

            // The filler indexes are gone (cleanup): the next update builds slot 1's index under that alternate name.
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpdateIndexesAsync(adminSession);
            }

            Assert.Contains(await IndexesAsync(collection), i => i["name"].AsString == failed.Name);
            states = await RootStatesAsync(tenant, "SwapFail-1.0.0");
            Assert.DoesNotContain(states, s => s.State == IndexState.Failed);
            Assert.Contains(states, s => s.State == IndexState.Applied && s.Name == slot1);
            Assert.Contains(states, s => s.State == IndexState.Applied && s.Name == failed.Name);
        });
    }

    /// <summary>
    ///     AC 5: a second identical index update and a second upgrade write no CkType document (database profiler on the
    ///     CkType collection); the stored <c>AppliedAt</c> stays.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedRun_WritesNoCkTypeDocument(bool betaFirst)
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("NoWrite", "1.0.0", betaFirst));
            var collection = Collection(tenant, "NoWrite");
            await ReplaceFieldIndexesAsync(collection, (IndexBaseName("NoWrite", AlphaTypeName), LegacyKeys));
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                // A real change is written: Applied under the shared legacy index, then under the tiebreak index.
                await tenant.UpdateIndexesAsync(adminSession);
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
            }

            var before = await StatesByTypeAsync(tenant, "NoWrite-1.0.0");
            var ckTypes = ((TenantRepository)tenant.GetTenantRepository()).DataSource.CkTypes.GetMongoCollection();
            await using (await ProfileAsync(ckTypes,
                             name => new BsonDocument
                             {
                                 { "ns", $"{ckTypes.Database.DatabaseNamespace.DatabaseName}.{name}" },
                                 { "op", new BsonDocument("$in", new BsonArray { "update", "insert", "remove" }) }
                             },
                             writes => Assert.Empty(writes)))
            {
                using var adminSession = await systemContext.GetAdminSessionAsync();
                await tenant.UpdateIndexesAsync(adminSession);
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
                await tenant.UpdateIndexesAsync(adminSession);
            }

            Assert.Equal(before, await StatesByTypeAsync(tenant, "NoWrite-1.0.0"));
        });
    }

    /// <summary>
    ///     AC 6: the holder of a legacy index another type shares, named in the sharing log: the type whose slot name the
    ///     index carries (whatever stored states say, so the same in both orders); for an index with another name the only
    ///     type with a stored <c>Applied</c> state under it; unknown without such a state or with several.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HolderOfASharedLegacyIndex_IsNamedBySlotNameOrStoredStates(bool betaFirst)
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("Holder", "1.0.0", betaFirst));
            var repository = (TenantRepository)tenant.GetTenantRepository();
            var collectionName = Collection(tenant, "Holder").CollectionNamespace.CollectionName;
            var legacyName = IndexBaseName("Holder", AlphaTypeName);
            const string foreignName = "hand_made_index";
            var alpha = new CkId<CkTypeId>($"Holder-1.0.0/{AlphaTypeName}");
            var beta = new CkId<CkTypeId>($"Holder-1.0.0/{BetaTypeName}");

            async Task<CkId<CkTypeId>?> HolderAsync(string indexName)
            {
                var service = new IndexStateService(repository.DataSource.CkTypes, NullLogger.Instance);
                service.BeginTracking(collectionName, [alpha, beta]);
                return await service.FindStoredHolderAsync(repository.GetSession(), indexName, collectionName);
            }

            // By slot name, without and against a stored state.
            Assert.Equal(alpha, await HolderAsync(legacyName));
            await AddStoredStateAsync(tenant, "Holder", BetaTypeName, legacyName, IndexState.Applied);
            Assert.Equal(alpha, await HolderAsync(legacyName));

            // Another name: unknown, then the only type with a stored state, then unknown again with two.
            Assert.Null(await HolderAsync(foreignName));
            await AddStoredStateAsync(tenant, "Holder", BetaTypeName, foreignName, IndexState.Applied);
            Assert.Equal(beta, await HolderAsync(foreignName));
            await AddStoredStateAsync(tenant, "Holder", AlphaTypeName, foreignName, IndexState.Applied);
            Assert.Null(await HolderAsync(foreignName));
        });
    }

    /// <summary>
    ///     CodeRabbit on PR #269: a failure recorded under the alternate name of a slot (AC 4) stays through a completed
    ///     update that keeps a legacy index for the slot (nothing is built), and is removed once the slot is served in its
    ///     target shape - here by its own index built under its expected name.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureUnderTheAlternateName_StaysWhileALegacyIndexServesTheSlot(bool otherFirst)
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(SwapModel("AltFail", "1.0.0", otherFirst));
            var collection = Collection(tenant, "AltFail");
            var slot1Legacy = IndexBaseName("AltFail", RootTypeName, 1);
            var slot1 = slot1Legacy + TiebreakIndexShape.NameSuffix;
            string[] slot0Keys = ["ckTypeId", otherFirst ? OtherFieldPath : FieldPath, "_id", "rtState"];
            string[] slot1LegacyKeys = ["ckTypeId", otherFirst ? FieldPath : OtherFieldPath, "rtState"];
            await ReplaceIndexesAsync(collection, [FieldPath, OtherFieldPath], (slot1, slot0Keys));
            await FillUpIndexesAsync(collection);
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpdateIndexesAsync(adminSession);
            }

            var failed = Assert.Single(await RootStatesAsync(tenant, "AltFail-1.0.0"), s => s.State == IndexState.Failed);

            // A legacy index for slot 1 (the filler indexes are gone): the update keeps it and builds nothing; the
            // failure stays.
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument(slot1LegacyKeys.Select(k => new BsonElement(k, 1))),
                new CreateIndexOptions { Name = slot1Legacy }));
            CollectionIndexCache.Invalidate(collection);
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpdateIndexesAsync(adminSession);
            }

            Assert.Contains(await IndexesAsync(collection), i => i["name"].AsString == slot1Legacy);
            Assert.Contains(await RootStatesAsync(tenant, "AltFail-1.0.0"),
                s => s.State == IndexState.Failed && s.Name == failed.Name);

            // Slot 0's index under slot 1's name is gone: the upgrade builds both slots under their expected names and
            // the alternate name's failure is over.
            await collection.Indexes.DropOneAsync(slot1);
            CollectionIndexCache.Invalidate(collection);
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
            }

            var states = await RootStatesAsync(tenant, "AltFail-1.0.0");
            Assert.Contains(await IndexesAsync(collection), i => i["name"].AsString == slot1);
            Assert.DoesNotContain(states, s => s.Name == failed.Name);
            Assert.DoesNotContain(states, s => s.State == IndexState.Failed);
        });
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>Adds indexes on unused fields until the collection holds <see cref="MaxIndexesPerCollection" />.</summary>
    private static async Task FillUpIndexesAsync(IMongoCollection<BsonDocument> collection)
    {
        var count = (await IndexesAsync(collection)).Count;
        for (var i = 0; count < MaxIndexesPerCollection; i++, count++)
        {
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument($"filler{i}", 1), new CreateIndexOptions { Name = $"filler_{i}" }));
        }

        CollectionIndexCache.Invalidate(collection);
    }

    /// <summary>
    ///     Stores a state under <paramref name="indexName" /> on the type; with <paramref name="onlyState" /> it is the
    ///     type's only state in the collection (the other states of the collection are removed).
    /// </summary>
    private static async Task AddStoredStateAsync(ITenantContext tenant, string modelName, string typeName,
        string indexName, IndexState state, bool onlyState = false)
    {
        var repository = (TenantRepository)tenant.GetTenantRepository();
        var ckTypes = repository.DataSource.CkTypes;
        var collectionName = Collection(tenant, modelName).CollectionNamespace.CollectionName;
        var ckType = await ckTypes.DocumentAsync(repository.GetSession(),
            new CkId<CkTypeId>($"{modelName}-1.0.0/{typeName}"));
        Assert.NotNull(ckType);
        ckType.IndexStates =
        [
            .. (ckType.IndexStates ?? []).Where(s =>
                s.Name != indexName && !(onlyState && s.CollectionName == collectionName)),
            new CkIndexState
            {
                Name = indexName,
                CollectionName = collectionName,
                State = state,
                AppliedAt = DateTime.UtcNow
            }
        ];
        await ckTypes.ReplaceManyAsync(repository.GetSession(), [ckType]);
    }

    private static async Task AssertNoStateUnderAsync(ITenantContext tenant, string modelVersion, string indexName)
    {
        foreach (var ckType in await TypesAsync(tenant, modelVersion))
        {
            Assert.DoesNotContain(ckType.IndexStates ?? [], s => s.Name == indexName);
        }
    }

    /// <summary>Each type: <c>Failed</c> under its own tiebreak index name, which does not exist.</summary>
    private static async Task AssertFailedUnderOwnTiebreakNameAsync(ITenantContext tenant, string modelName)
    {
        var indexNames = (await IndexesAsync(Collection(tenant, modelName))).Select(i => i["name"].AsString).ToList();
        foreach (var typeName in new[] { AlphaTypeName, BetaTypeName })
        {
            var ownName = IndexBaseName(modelName, typeName) + TiebreakIndexShape.NameSuffix;
            var ckType = Assert.Single(await TypesAsync(tenant, $"{modelName}-1.0.0"),
                t => t.CkTypeId == new CkId<CkTypeId>($"{modelName}-1.0.0/{typeName}"));
            Assert.Contains(ckType.IndexStates ?? [], s => s.State == IndexState.Failed && s.Name == ownName);
            Assert.DoesNotContain(indexNames, n => n == ownName);
        }
    }

    private static async Task<List<CkIndexState>> RootStatesAsync(ITenantContext tenant, string modelVersion)
    {
        var repository = tenant.GetTenantRepository();
        var ckType = Assert.Single((await repository.GetCkTypeAsync(repository.GetSession(),
            [new CkId<CkTypeId>($"{modelVersion}/{RootTypeName}")], RtEntityQueryOptions.Create())).Items);
        return ckType.IndexStates?.ToList() ?? [];
    }

    private static async Task<List<string>> StatesByTypeAsync(ITenantContext tenant, string modelVersion) =>
        (await TypesAsync(tenant, modelVersion))
        .SelectMany(t => (t.IndexStates ?? []).Select(s =>
            $"{t.CkTypeId}:{s.Name}:{s.State}:{s.ErrorMessage}:{s.AppliedAt?.Ticks}"))
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();
}
