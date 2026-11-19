using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#9342: two CK types of one collection that declare an Ascending index on the same attributes produce the same
///     key spec <c>{ckTypeId, attr, _id, rtState}</c> (the field <c>ckTypeId</c>, not the type value). MongoDB holds one
///     non-unique index per key spec, so one index serves both types. The index update dedupes per collection on the key
///     spec and reuses the index that holds it - existing, or built earlier in the same collection run - instead of
///     building a second one (which MongoDB refused with 85 "Index already exists with a different name"). Every test runs
///     with both declaration orders of the two types.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class SharedIndexKeySpecTests(ImportTestCkModelFixture fixture)
{
    private const string FieldName = "SimpleField";
    private const string FieldPath = "attributes.simpleField";
    private const string RootTypeName = "SharedRoot";
    private const string AlphaTypeName = "Alpha";
    private const string BetaTypeName = "Beta";

    private static readonly string[] LegacyKeys = ["ckTypeId", FieldPath, "rtState"];
    private static readonly string[] TiebreakKeys = ["ckTypeId", FieldPath, "_id", "rtState"];

    /// <summary>
    ///     AC 1, AC 4: fresh tenant. The import and two index updates build exactly one index with the keys, in the
    ///     tiebreak shape; no type gets a failed index state, both are <c>Applied</c> under the shared index name; the
    ///     updates neither build nor drop anything.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshTenant_TwoTypesWithTheSameAscendingIndex_ShareOneIndex(bool betaFirst)
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("ShareModel", "1.0.0", betaFirst));
            var collection = Collection(tenant, "ShareModel");

            var shared = Assert.Single(await IndexesAsync(collection), i => KeysOf(i).Contains(FieldPath));
            Assert.Equal(TiebreakKeys, KeysOf(shared));
            var sharedName = shared["name"].AsString;
            await AssertAppliedUnderAsync(tenant, "ShareModel-1.0.0", sharedName);

            var afterImport = Describe(await IndexesAsync(collection));
            await using (await ProfileIndexCommandsAsync(collection, commands => Assert.Empty(commands)))
            {
                using var adminSession = await systemContext.GetAdminSessionAsync();
                await tenant.UpdateIndexesAsync(adminSession);
                Assert.Equal(afterImport, Describe(await IndexesAsync(collection)));
                await tenant.UpdateIndexesAsync(adminSession);
            }

            Assert.Equal(afterImport, Describe(await IndexesAsync(collection)));
            await AssertAppliedUnderAsync(tenant, "ShareModel-1.0.0", sharedName);
        });
    }

    /// <summary>
    ///     AC 2: the state today's normal path left behind on test-2 (octosystem): the legacy index of type A and the
    ///     tiebreak index <c>B_n_id</c> with A's tiebreak keys. The upgrade keeps <c>B_n_id</c> for both types, drops the
    ///     legacy index, ends without a failed state and leaves no index with legacy keys; a second upgrade is a no-op.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedState_LegacyOfOneTypeAndTiebreakOfTheOther_UpgradeKeepsTheTiebreakForBoth(bool betaFirst)
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("MixModel", "1.0.0", betaFirst));
            var collection = Collection(tenant, "MixModel");
            var legacyName = IndexBaseName("MixModel", AlphaTypeName);
            var tiebreakName = IndexBaseName("MixModel", BetaTypeName) + TiebreakIndexShape.NameSuffix;
            await ReplaceFieldIndexesAsync(collection,
                (legacyName, LegacyKeys), (tiebreakName, TiebreakKeys));

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
            }

            var upgraded = await IndexesAsync(collection);
            var shared = Assert.Single(upgraded, i => KeysOf(i).Contains(FieldPath));
            Assert.Equal(tiebreakName, shared["name"].AsString);
            Assert.Equal(TiebreakKeys, KeysOf(shared));
            Assert.DoesNotContain(upgraded, i => KeysOf(i).SequenceEqual(LegacyKeys));
            // Beta's index keeps its expected name (its state is not touched); Alpha is served by it now.
            await AssertNoFailedStateAsync(tenant, "MixModel-1.0.0");
            await AssertAppliedUnderAsync(tenant, "MixModel-1.0.0", tiebreakName, AlphaTypeName);

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
            }

            Assert.Equal(Describe(upgraded), Describe(await IndexesAsync(collection)));
        });
    }

    /// <summary>
    ///     The mixed state in an update without the upgrade: the tiebreak index <c>B_n_id</c> serves both types and the
    ///     cleanup drops A's legacy index (as for a single type whose tiebreak index exists), whatever the order; nothing
    ///     is built.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedState_UpdateWithoutUpgrade_TheTiebreakIndexServesBoth_AndTheLegacyIndexIsDropped(bool betaFirst)
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("MixUpd", "1.0.0", betaFirst));
            var collection = Collection(tenant, "MixUpd");
            var legacyName = IndexBaseName("MixUpd", AlphaTypeName);
            var tiebreakName = IndexBaseName("MixUpd", BetaTypeName) + TiebreakIndexShape.NameSuffix;
            await ReplaceFieldIndexesAsync(collection,
                (legacyName, LegacyKeys), (tiebreakName, TiebreakKeys));

            await using (await ProfileIndexCommandsAsync(collection,
                             commands =>
                             {
                                 // The profiler sees index commands: the cleanup's drop of the legacy index is there.
                                 Assert.Contains(commands, c => c.Contains("dropIndexes") && c.Contains(legacyName));
                                 Assert.DoesNotContain(commands, c => c.Contains("createIndexes"));
                             }))
            {
                using var adminSession = await systemContext.GetAdminSessionAsync();
                await tenant.UpdateIndexesAsync(adminSession);
            }

            var updated = await IndexesAsync(collection);
            var shared = Assert.Single(updated, i => KeysOf(i).Contains(FieldPath));
            Assert.Equal(tiebreakName, shared["name"].AsString);
            Assert.DoesNotContain(updated, i => KeysOf(i).SequenceEqual(LegacyKeys));
            await AssertNoFailedStateAsync(tenant, "MixUpd-1.0.0");
            await AssertAppliedUnderAsync(tenant, "MixUpd-1.0.0", tiebreakName, AlphaTypeName);
        });
    }

    /// <summary>
    ///     AC 3: one legacy index serving both types (built by an engine before AB#8960). An index update without the
    ///     upgrade keeps it for both types and builds no tiebreak index for the second one; the upgrade then builds
    ///     exactly one tiebreak index and drops the legacy one.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedLegacyIndex_IsKeptForBothTypes_AndTheUpgradeBuildsExactlyOneTiebreakIndex(bool betaFirst)
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("LegModel", "1.0.0", betaFirst));
            var collection = Collection(tenant, "LegModel");
            var legacyName = IndexBaseName("LegModel", AlphaTypeName);
            await ReplaceFieldIndexesAsync(collection, (legacyName, LegacyKeys));
            var legacyOnly = Describe(await IndexesAsync(collection));

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpdateIndexesAsync(adminSession);
            }

            Assert.Equal(legacyOnly, Describe(await IndexesAsync(collection)));
            await AssertAppliedUnderAsync(tenant, "LegModel-1.0.0", legacyName);

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
            }

            var upgraded = await IndexesAsync(collection);
            var shared = Assert.Single(upgraded, i => KeysOf(i).Contains(FieldPath));
            Assert.Equal(TiebreakKeys, KeysOf(shared));
            Assert.DoesNotContain(upgraded, i => i["name"].AsString == legacyName);
            await AssertAppliedUnderAsync(tenant, "LegModel-1.0.0", shared["name"].AsString);

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
                await tenant.UpdateIndexesAsync(adminSession);
            }

            Assert.Equal(Describe(upgraded), Describe(await IndexesAsync(collection)));
        });
    }

    /// <summary>
    ///     AC 4: a type whose own index failed in an earlier run (the 85 of the bug) has no failed state left once it is
    ///     served by the shared index.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStateOfAnEarlierRun_IsRemoved_WhenTheTypeIsServedByTheSharedIndex(bool betaFirst)
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("FailModel", "1.0.0", betaFirst));
            var repository = (TenantRepository)tenant.GetTenantRepository();
            var collectionName = Collection(tenant, "FailModel").CollectionNamespace.CollectionName;
            var holderName = Assert.Single(await IndexesAsync(Collection(tenant, "FailModel")),
                i => KeysOf(i).Contains(FieldPath))["name"].AsString;
            // The type served by the other type's index: the state the bug left on it is its own tiebreak index name,
            // Failed with the 85 message.
            foreach (var typeName in new[] { AlphaTypeName, BetaTypeName }
                         .Where(t => IndexBaseName("FailModel", t) + TiebreakIndexShape.NameSuffix != holderName))
            {
                var ckTypes = repository.DataSource.CkTypes;
                var typeId = new CkId<CkTypeId>($"FailModel-1.0.0/{typeName}");
                var ckType = await ckTypes.DocumentAsync(repository.GetSession(), typeId);
                Assert.NotNull(ckType);
                ckType.IndexStates =
                [
                    .. ckType.IndexStates ?? [],
                    new CkIndexState
                    {
                        Name = IndexBaseName("FailModel", typeName) + TiebreakIndexShape.NameSuffix,
                        CollectionName = collectionName,
                        State = IndexState.Failed,
                        ErrorMessage = "Index already exists with a different name",
                        AppliedAt = DateTime.UtcNow
                    }
                ];
                await ckTypes.ReplaceManyAsync(repository.GetSession(), [ckType]);
            }

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpdateIndexesAsync(adminSession);
            }

            var shared = Assert.Single(await IndexesAsync(Collection(tenant, "FailModel")),
                i => KeysOf(i).Contains(FieldPath));
            await AssertAppliedUnderAsync(tenant, "FailModel-1.0.0", shared["name"].AsString);
        });
    }

    /// <summary>
    ///     AC 5: unique indexes keep their per-type partial filter (AB#8960) - two types with a Unique index on the same
    ///     attribute get one index each, filtering their own type, and no failed state.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UniqueIndexes_OfTwoTypes_KeepTheirOwnTypeFilter(bool betaFirst)
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("UniqShare", "1.0.0", betaFirst, IndexTypeDto.Unique));
            var unique = (await IndexesAsync(Collection(tenant, "UniqShare")))
                .Where(i => i.GetValue("unique", false).ToBoolean()).ToList();
            Assert.Equal(2, unique.Count);
            foreach (var typeName in new[] { AlphaTypeName, BetaTypeName })
            {
                Assert.Single(unique, i => i["partialFilterExpression"].ToJson().Contains($"UniqShare/{typeName}"));
            }

            await AssertNoFailedStateAsync(tenant, "UniqShare-1.0.0");
        });
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private async Task WithTenantAsync(Func<ITenantContext, Task> test) =>
        await WithTenantAsync((tenant, _) => test(tenant));

    private async Task WithTenantAsync(Func<ITenantContext, ISystemContext, Task> test)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"SK_{Guid.NewGuid():N}"[..20];
        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            adminSession.StartTransaction();
            await systemContext.CreateChildTenantAsync(adminSession, tenantId, tenantId);
            await adminSession.CommitTransactionAsync();
        }

        try
        {
            await test(await systemContext.GetChildTenantContextAsync(tenantId), systemContext);
        }
        finally
        {
            using var cleanupSession = await systemContext.GetAdminSessionAsync();
            cleanupSession.StartTransaction();
            await systemContext.DropChildTenantAsync(cleanupSession, tenantId);
            await cleanupSession.CommitTransactionAsync();
        }
    }

    /// <summary>
    ///     A collection root without indexes and two types derived from it (stored in its collection) that each declare
    ///     the same index on <see cref="FieldName" />; <paramref name="betaFirst" /> swaps their declaration order.
    /// </summary>
    private static CkCompiledModelRoot Model(string modelName, string version, bool betaFirst,
        IndexTypeDto indexType = IndexTypeDto.Ascending)
    {
        var modelVersion = $"{modelName}-{version}";

        CkCompiledTypeDto Derived(string typeName) => new()
        {
            TypeId = new CkTypeId(typeName),
            DerivedFromCkTypeId = new CkId<CkTypeId>($"{modelVersion}/{RootTypeName}"),
            Attributes =
            [
                new()
                {
                    CkAttributeId = new CkId<CkAttributeId>($"{modelVersion}/{FieldName}"),
                    AttributeName = FieldName
                }
            ],
            Indexes =
            [
                new()
                {
                    IndexType = indexType,
                    Fields = [new() { AttributePaths = [FieldName] }]
                }
            ]
        };

        var alpha = Derived(AlphaTypeName);
        var beta = Derived(BetaTypeName);
        return new CkCompiledModelRoot
        {
            ModelId = new CkModelId(modelVersion),
            Dependencies = [SystemCkIds.CkModelId],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId(FieldName), ValueType = AttributeValueTypesDto.String }
            ],
            Types =
            [
                new()
                {
                    TypeId = new CkTypeId(RootTypeName),
                    DerivedFromCkTypeId = SystemCkIds.CkEntityTypeId,
                    IsCollectionRoot = true,
                    Attributes = []
                },
                .. betaFirst ? new[] { beta, alpha } : [alpha, beta]
            ]
        };
    }

    /// <summary>The engine's name of the first index a type declares: its collection name and <c>_0</c>.</summary>
    private static string IndexBaseName(string modelName, string typeName) =>
        new RtCkId<CkTypeId>($"{modelName}/{typeName}").GetCkTypeCollectionName() + "_0";

    private static IMongoCollection<BsonDocument> Collection(ITenantContext tenant, string modelName)
    {
        var repository = (TenantRepository)tenant.GetTenantRepository();
        var collection = repository.DataSource
            .GetRtDatabaseCollectionByTypeId<RtEntity>(new RtCkId<CkTypeId>($"{modelName}/{RootTypeName}"))
            .GetMongoCollection();
        return collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
    }

    /// <summary>Replaces every index on <see cref="FieldPath" /> with the given ones (name, keys).</summary>
    private static async Task ReplaceFieldIndexesAsync(IMongoCollection<BsonDocument> collection,
        params (string Name, string[] Keys)[] indexes)
    {
        foreach (var index in (await IndexesAsync(collection)).Where(i => KeysOf(i).Contains(FieldPath)))
        {
            await collection.Indexes.DropOneAsync(index["name"].AsString);
        }

        foreach (var (name, keys) in indexes)
        {
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument(keys.Select(k => new BsonElement(k, 1))),
                new CreateIndexOptions { Name = name }));
        }

        CollectionIndexCache.Invalidate(collection);
    }

    private static async Task<List<CkType>> TypesAsync(ITenantContext tenant, string modelVersion)
    {
        var repository = tenant.GetTenantRepository();
        var ckTypes = await repository.GetCkTypeAsync(repository.GetSession(),
            [
                new CkId<CkTypeId>($"{modelVersion}/{AlphaTypeName}"),
                new CkId<CkTypeId>($"{modelVersion}/{BetaTypeName}")
            ],
            RtEntityQueryOptions.Create());
        Assert.Equal(2, ckTypes.Items.Count());
        return ckTypes.Items.ToList();
    }

    private static async Task AssertNoFailedStateAsync(ITenantContext tenant, string modelVersion)
    {
        foreach (var ckType in await TypesAsync(tenant, modelVersion))
        {
            Assert.DoesNotContain(ckType.IndexStates ?? [], s => s.State == IndexState.Failed);
        }
    }

    /// <summary>
    ///     The types (default both): no failed state, and <c>Applied</c> under the shared index name.
    /// </summary>
    private static async Task AssertAppliedUnderAsync(ITenantContext tenant, string modelVersion, string sharedName,
        params string[] typeNames)
    {
        var ckTypes = (await TypesAsync(tenant, modelVersion))
            .Where(t => typeNames.Length == 0 ||
                        typeNames.Any(n => t.CkTypeId == new CkId<CkTypeId>($"{modelVersion}/{n}")))
            .ToList();
        Assert.Equal(typeNames.Length == 0 ? 2 : typeNames.Length, ckTypes.Count);
        foreach (var ckType in ckTypes)
        {
            var states = ckType.IndexStates ?? [];
            Assert.DoesNotContain(states, s => s.State == IndexState.Failed);
            Assert.Contains(states, s => s.State == IndexState.Applied && s.Name == sharedName);
        }
    }

    /// <summary>
    ///     Records every createIndexes / dropIndexes command on <paramref name="collection" /> (database profiler, level 2)
    ///     until the returned scope is disposed; then hands them to <paramref name="assert" /> as JSON and switches the
    ///     profiler off again. The profiler needs the admin user (the tenant user may not run <c>profile</c>).
    /// </summary>
    private async Task<IAsyncDisposable> ProfileIndexCommandsAsync(IMongoCollection<BsonDocument> collection,
        Action<List<string>> assert)
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
        var database = new MongoClient(urlBuilder.ToMongoUrl())
            .GetDatabase(collection.Database.DatabaseNamespace.DatabaseName);
        var name = collection.CollectionNamespace.CollectionName;
        var from = DateTime.UtcNow.AddSeconds(-1);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 2));
        return new AsyncScope(async () =>
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 0));
            var filter = new BsonDocument
            {
                { "ts", new BsonDocument("$gte", from) },
                {
                    "$or", new BsonArray
                    {
                        new BsonDocument("command.createIndexes", name),
                        new BsonDocument("command.dropIndexes", name)
                    }
                }
            };
            var entries = await database.GetCollection<BsonDocument>("system.profile").Find(filter).ToListAsync();
            assert(entries.Select(e => e["command"].ToJson()).ToList());
        });
    }

    private sealed class AsyncScope(Func<Task> onDispose) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await onDispose();
    }

    private static async Task<List<BsonDocument>> IndexesAsync(IMongoCollection<BsonDocument> collection)
    {
        using var cursor = await collection.Indexes.ListAsync();
        return await cursor.ToListAsync();
    }

    private static string[] KeysOf(BsonDocument index) => index["key"].AsBsonDocument.Names.ToArray();

    private static List<string> Describe(IEnumerable<BsonDocument> indexes) =>
        indexes.Select(i => $"{i["name"]}:{i["key"].ToJson()}").OrderBy(s => s, StringComparer.Ordinal).ToList();
}
