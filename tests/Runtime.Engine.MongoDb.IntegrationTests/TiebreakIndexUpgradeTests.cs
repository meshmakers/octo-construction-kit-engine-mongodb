using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     The index side of the paging tiebreak (AB#8960). New CK Ascending indexes are created in the tiebreak shape
///     <c>{ckTypeId, attr, _id, rtState}</c> (<c>&lt;Type&gt;_&lt;n&gt;_id</c>); unique, UniqueNotDeleted, text and the
///     system-level indexes keep theirs. An existing legacy index (<c>{ckTypeId, attr, rtState}</c>, built by an engine
///     before AB#8960) is kept by a CK import and by <c>UpdateIndexesAsync</c>, and the collection pages without the
///     tiebreak, still from that index. <c>UpgradeTiebreakIndexesAsync</c> builds the new index before it drops the
///     legacy one (the collection never lacks the index), is idempotent, and afterwards the collection pages with the
///     tiebreak from the new index. An older engine after a rollback still sorts its pages from the new index.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class TiebreakIndexUpgradeTests(ImportTestCkModelFixture fixture)
{
    private const string FieldName = "SimpleField";
    private const string FieldPath = "attributes.simpleField";
    private const string TypeName = "SimpleType";

    private static readonly string[] LegacyKeys = ["ckTypeId", FieldPath, "rtState"];
    private static readonly string[] TiebreakKeys = ["ckTypeId", FieldPath, "_id", "rtState"];

    [Fact]
    public async Task NewIndexes_AscendingGetsTheTiebreakShape_UniqueTextAndSystemIndexesKeepTheirs()
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.0", IndexTypeDto.Ascending));
            await tenant.ImportCkModelAsync(Model("UniqModel", "1.0.0", IndexTypeDto.Unique));
            await tenant.ImportCkModelAsync(Model("UndModel", "1.0.0", IndexTypeDto.UniqueNotDeleted));
            await tenant.ImportCkModelAsync(Model("TextModel", "1.0.0", IndexTypeDto.Text));

            var ascending = await IndexesAsync(Collection(tenant, "AscModel"));
            var tiebreak = Assert.Single(ascending, i => KeysOf(i).Contains(FieldPath));
            Assert.Equal(TiebreakKeys, KeysOf(tiebreak));
            Assert.EndsWith(TiebreakIndexShape.NameSuffix, tiebreak["name"].AsString);
            // System-level indexes outside the CK definition keep their shape.
            Assert.Contains(ascending, i => KeysOf(i).SequenceEqual(["rtState"]));
            Assert.Contains(ascending, i => KeysOf(i).SequenceEqual(["ckTypeId", "rtCreatedBy"]));
            // The System Entity index {CkTypeId, _id} holds _id already and keeps its name.
            Assert.Contains(ascending, i => KeysOf(i).SequenceEqual(["ckTypeId", "_id", "rtState"]) &&
                                            !i["name"].AsString.EndsWith(TiebreakIndexShape.NameSuffix));
            Assert.DoesNotContain(ascending, i => TiebreakIndexShape.LeadingKeyWithoutTiebreak(i) != null);

            foreach (var model in new[] { "UniqModel", "UndModel" })
            {
                var unique = Assert.Single(await IndexesAsync(Collection(tenant, model)),
                    i => i.GetValue("unique", false).ToBoolean());
                Assert.Equal([FieldPath], KeysOf(unique));
            }

            var text = Assert.Single(await IndexesAsync(Collection(tenant, "TextModel")),
                i => KeysOf(i).Contains("_fts"));
            Assert.DoesNotContain("_id", KeysOf(text));

            Assert.True(await GateAsync(Collection(tenant, "AscModel")));
        });
    }

    [Fact]
    public async Task LegacyIndex_IsKeptByImportAndUpdateIndexes_AndThePagesUseItWithoutTiebreak()
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.0", IndexTypeDto.Ascending));
            var collection = Collection(tenant, "AscModel");
            await SeedAsync(tenant, "AscModel", 200);
            var legacyName = await MakeLegacyAsync(collection);

            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.1", IndexTypeDto.Ascending));
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpdateIndexesAsync(adminSession);
            }

            var indexes = await IndexesAsync(collection);
            Assert.Contains(indexes, i => i["name"].AsString == legacyName && KeysOf(i).SequenceEqual(LegacyKeys));
            Assert.DoesNotContain(indexes, i => KeysOf(i).SequenceEqual(TiebreakKeys));

            Assert.False(await GateAsync(collection));
            var page = await PageAsync(tenant, "AscModel");
            Assert.Equal(new BsonDocument(FieldPath, 1), page.Sort);
            var plan = await ExplainAsync(collection, page.Stages);
            Assert.Equal(legacyName, plan.Index);
            Assert.False(plan.HasSort);

            // The gate keeps the legacy order only for the key the legacy index serves: unsorted pages and pages
            // sorted by another key get the tiebreak right away.
            Assert.True(await GateAsync(collection, sortKey: null));
            Assert.True(await GateAsync(collection, "rtCreationDateTime"));
        });
    }

    [Fact]
    public async Task Upgrade_ReplacesTheLegacyIndex_PagesWithTiebreakFromTheNewIndex_AndIsIdempotent()
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.0", IndexTypeDto.Ascending));
            var collection = Collection(tenant, "AscModel");
            await SeedAsync(tenant, "AscModel", 200);
            var legacyName = await MakeLegacyAsync(collection);

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
            }

            var upgraded = await IndexesAsync(collection);
            Assert.DoesNotContain(upgraded, i => i["name"].AsString == legacyName);
            var tiebreak = Assert.Single(upgraded, i => KeysOf(i).Contains(FieldPath));
            Assert.Equal(TiebreakKeys, KeysOf(tiebreak));
            Assert.Equal(legacyName + TiebreakIndexShape.NameSuffix, tiebreak["name"].AsString);

            Assert.True(await GateAsync(collection));
            var page = await PageAsync(tenant, "AscModel");
            Assert.Equal(new BsonDocument { { FieldPath, 1 }, { "_id", 1 } }, page.Sort);
            var plan = await ExplainAsync(collection, page.Stages);
            Assert.Equal(tiebreak["name"].AsString, plan.Index);
            Assert.False(plan.HasSort);

            // Descending: _id follows the direction of the last key, so the same index serves the page backwards.
            var descending = await PageAsync(tenant, "AscModel", descending: true);
            Assert.Equal(new BsonDocument { { FieldPath, -1 }, { "_id", -1 } }, descending.Sort);
            var backwards = await ExplainAsync(collection, descending.Stages);
            Assert.Equal(tiebreak["name"].AsString, backwards.Index);
            Assert.False(backwards.HasSort);

            // Idempotent: a second upgrade, an import and an index update change nothing.
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
                await tenant.UpdateIndexesAsync(adminSession);
            }

            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.1", IndexTypeDto.Ascending));
            Assert.Equal(Describe(upgraded), Describe(await IndexesAsync(collection)));
        });
    }

    [Fact]
    public async Task Upgrade_NeverLeavesTheCollectionWithoutAnIndexOnTheAttribute()
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.0", IndexTypeDto.Ascending));
            var collection = Collection(tenant, "AscModel");
            await SeedAsync(tenant, "AscModel", 60_000);
            await MakeLegacyAsync(collection);

            using var adminSession = await systemContext.GetAdminSessionAsync();
            var upgrade = tenant.UpgradeTiebreakIndexesAsync(adminSession);
            var observations = 0;
            var sawBoth = false;
            do
            {
                var indexes = await IndexesAsync(collection);
                var onTheAttribute = indexes.Count(i => KeysOf(i).Take(2).SequenceEqual(["ckTypeId", FieldPath]));
                Assert.True(onTheAttribute >= 1, $"observation {observations}: no index on {FieldPath}");
                sawBoth |= onTheAttribute == 2;
                observations++;
            } while (!upgrade.IsCompleted);

            await upgrade;
            Assert.True(observations >= 2, $"only {observations} observation(s) during the upgrade");
            Assert.Single(await IndexesAsync(collection), i => KeysOf(i).SequenceEqual(TiebreakKeys));
            // Informative: whether the build was slow enough to see both indexes at once.
            RunProgress.Report($"AB#8960 gapless upgrade: observations {observations}, both indexes seen: {sawBoth}");
        });
    }

    [Fact]
    public async Task Rollback_AnOlderEngineSortsItsPagesFromTheNewIndex()
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.0", IndexTypeDto.Ascending));
            var collection = Collection(tenant, "AscModel");
            await SeedAsync(tenant, "AscModel", 200);
            var tiebreakName = Assert.Single(await IndexesAsync(collection),
                i => KeysOf(i).SequenceEqual(TiebreakKeys))["name"].AsString;

            // The page of an engine before AB#8960: the caller's sort alone.
            var page = await PageAsync(tenant, "AscModel", idTiebreak: false);
            Assert.Equal(new BsonDocument(FieldPath, 1), page.Sort);
            var plan = await ExplainAsync(collection, page.Stages);
            Assert.Equal(tiebreakName, plan.Index);
            Assert.False(plan.HasSort);
        });
    }

    /// <summary>
    ///     Several instances: the gate caches per process. When another instance upgrades the indexes, this one keeps
    ///     paging without the tiebreak until its cache entry expires (safe: the legacy order, still from an index).
    ///     When a legacy index comes back (rollback of another instance), this one keeps the tiebreak until expiry -
    ///     at most <see cref="PagingTiebreakGate.Ttl" /> of the regression. Expiry is simulated by
    ///     <see cref="PagingTiebreakGate.Invalidate" />, which an index update of the same process also calls.
    /// </summary>
    [Fact]
    public async Task Gate_IsCachedPerProcess_AChangeByAnotherInstanceIsSeenAfterExpiry()
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.0", IndexTypeDto.Ascending));
            var collection = Collection(tenant, "AscModel");
            var legacyName = await MakeLegacyAsync(collection);
            Assert.False(await GateAsync(collection));

            // Another instance upgrades: drop the legacy index, build the tiebreak one - this process is not told.
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ckTypeId", 1 }, { FieldPath, 1 }, { "_id", 1 }, { "rtState", 1 } },
                new CreateIndexOptions { Name = legacyName + TiebreakIndexShape.NameSuffix }));
            await collection.Indexes.DropOneAsync(legacyName);
            Assert.False(await PagingTiebreakGate.IsEnabledAsync(collection, FieldPath));
            Assert.True(await GateAsync(collection));

            // Another instance rolls back: the legacy index is back - this process keeps the tiebreak until expiry.
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ckTypeId", 1 }, { FieldPath, 1 }, { "rtState", 1 } },
                new CreateIndexOptions { Name = legacyName }));
            Assert.True(await PagingTiebreakGate.IsEnabledAsync(collection, FieldPath));
            Assert.False(await GateAsync(collection));
        });
    }

    /// <summary>
    ///     A sort over an attribute without any index: the collection has no legacy index, the page gets the tiebreak.
    ///     No regression - the page was a blocking sort over the matching documents before as well.
    /// </summary>
    [Fact]
    public async Task UnindexedSortAttribute_PagesWithTiebreak_TheSortWasInMemoryBefore()
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("PlainModel", "1.0.0", IndexTypeDto.None));
            var collection = Collection(tenant, "PlainModel");
            await SeedAsync(tenant, "PlainModel", 200);

            Assert.True(await GateAsync(collection));
            var page = await PageAsync(tenant, "PlainModel");
            Assert.Equal(new BsonDocument { { FieldPath, 1 }, { "_id", 1 } }, page.Sort);
            Assert.True((await ExplainAsync(collection, page.Stages)).HasSort);

            var before = await PageAsync(tenant, "PlainModel", idTiebreak: false);
            Assert.True((await ExplainAsync(collection, before.Stages)).HasSort);
        });
    }

    /// <summary>
    ///     A sort over an attribute with a Unique index (partial filter on <c>ckTypeId</c>): the index provides the page
    ///     order and the tiebreak would break it (measured: without tiebreak IXSCAN on the unique index, with it a SORT
    ///     over every document), and the key is unique anyway - the page keeps the caller's sort. With a UniqueNotDeleted
    ///     index (partial filter on <c>rtState</c> as well) the page was a blocking sort before, so it gets the tiebreak
    ///     (AB#8960 review finding 3).
    /// </summary>
    [Theory]
    [InlineData(IndexTypeDto.Unique)]
    [InlineData(IndexTypeDto.UniqueNotDeleted)]
    public async Task UniqueIndexedSortAttribute_KeepsTheIndexOrder_UniqueNotDeletedGetsTheTiebreak(
        IndexTypeDto indexType)
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("UniqModel", "1.0.0", indexType));
            var collection = Collection(tenant, "UniqModel");
            await SeedAsync(tenant, "UniqModel", 2_000, distinctValues: true);

            var without = await ExplainAsync(collection, (await PageAsync(tenant, "UniqModel", idTiebreak: false)).Stages);
            var with = await ExplainAsync(collection, (await PageAsync(tenant, "UniqModel", idTiebreak: true)).Stages);
            RunProgress.Report($"AB#8960 {indexType}: without tiebreak index={without.Index} sort={without.HasSort}; " +
                               $"with tiebreak index={with.Index} sort={with.HasSort}");

            var page = await PageAsync(tenant, "UniqModel");
            var plan = await ExplainAsync(collection, page.Stages);
            if (indexType == IndexTypeDto.Unique)
            {
                Assert.False(without.HasSort);
                Assert.True(with.HasSort);
                Assert.False(await GateAsync(collection));
                Assert.Equal(new BsonDocument(FieldPath, 1), page.Sort);
                Assert.False(plan.HasSort);
            }
            else
            {
                Assert.True(without.HasSort);
                Assert.True(await GateAsync(collection));
                Assert.Equal(new BsonDocument { { FieldPath, 1 }, { "_id", 1 } }, page.Sort);
            }
        });
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private async Task WithTenantAsync(Func<ITenantContext, Task> test) =>
        await WithTenantAsync((tenant, _) => test(tenant));

    private async Task WithTenantAsync(Func<ITenantContext, ISystemContext, Task> test)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"TB_{Guid.NewGuid():N}"[..20];
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

    private static CkCompiledModelRoot Model(string modelName, string version, IndexTypeDto indexType)
    {
        var modelVersion = $"{modelName}-{version}";
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
                    TypeId = new CkTypeId(TypeName),
                    DerivedFromCkTypeId = SystemCkIds.CkEntityTypeId,
                    IsCollectionRoot = true,
                    Attributes =
                    [
                        new()
                        {
                            CkAttributeId = new CkId<CkAttributeId>($"{modelVersion}/{FieldName}"),
                            AttributeName = FieldName
                        }
                    ],
                    Indexes = indexType == IndexTypeDto.None
                        ? []
                        :
                    [
                        new()
                        {
                            IndexType = indexType,
                            Fields =
                            [
                                new()
                                {
                                    AttributePaths = [FieldName],
                                    Weight = indexType == IndexTypeDto.Text ? 1 : null
                                }
                            ]
                        }
                    ]
                }
            ]
        };
    }

    private static RtCkId<CkTypeId> TypeId(string modelName) => new($"{modelName}/{TypeName}");

    private static IMongoCollection<BsonDocument> Collection(ITenantContext tenant, string modelName)
    {
        var repository = (TenantRepository)tenant.GetTenantRepository();
        var collection = repository.DataSource.GetRtDatabaseCollectionByTypeId<RtEntity>(TypeId(modelName))
            .GetMongoCollection();
        return collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
    }

    private static async Task<List<BsonDocument>> IndexesAsync(IMongoCollection<BsonDocument> collection)
    {
        using var cursor = await collection.Indexes.ListAsync();
        return await cursor.ToListAsync();
    }

    private static string[] KeysOf(BsonDocument index) => index["key"].AsBsonDocument.Names.ToArray();

    private static List<string> Describe(IEnumerable<BsonDocument> indexes) =>
        indexes.Select(i => $"{i["name"]}:{i["key"].ToJson()}").OrderBy(s => s, StringComparer.Ordinal).ToList();

    private static async Task<bool> GateAsync(IMongoCollection<BsonDocument> collection, string? sortKey = FieldPath)
    {
        PagingTiebreakGate.Invalidate(collection.CollectionNamespace.DatabaseNamespace.DatabaseName,
            collection.CollectionNamespace.CollectionName);
        return await PagingTiebreakGate.IsEnabledAsync(collection, sortKey);
    }

    /// <summary>
    ///     Replaces the tiebreak-shape index of the attribute with the legacy shape an engine before AB#8960 built,
    ///     under its legacy name. Returns that name.
    /// </summary>
    private static async Task<string> MakeLegacyAsync(IMongoCollection<BsonDocument> collection)
    {
        var tiebreak = Assert.Single(await IndexesAsync(collection), i => KeysOf(i).SequenceEqual(TiebreakKeys));
        var tiebreakName = tiebreak["name"].AsString;
        var legacyName = tiebreakName[..^TiebreakIndexShape.NameSuffix.Length];
        await collection.Indexes.DropOneAsync(tiebreakName);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { { "ckTypeId", 1 }, { FieldPath, 1 }, { "rtState", 1 } },
            new CreateIndexOptions { Name = legacyName }));
        PagingTiebreakGate.Invalidate(collection.CollectionNamespace.DatabaseNamespace.DatabaseName,
            collection.CollectionNamespace.CollectionName);
        return legacyName;
    }

    /// <summary>
    ///     Inserts <paramref name="count" /> entities with 50 distinct values: one through the repository, its stored
    ///     document is the template of the rest (the shape the engine writes).
    /// </summary>
    private static async Task SeedAsync(ITenantContext tenant, string modelName, int count,
        bool distinctValues = false)
    {
        var repository = tenant.GetTenantRepository();
        var entity = await repository.CreateTransientRtEntityAsync(
            new CkId<CkTypeId>(new CkModelId($"{modelName}-1.0.0"), new CkTypeId(TypeName)));
        entity.RtId = OctoObjectId.GenerateNewId();
        entity.SetAttributeValue(FieldName, AttributeValueTypesDto.String, "v-00");
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, TypeId(modelName), entity);
            await session.CommitTransactionAsync();
        }

        var collection = Collection(tenant, modelName);
        var template = await collection.Find(new BsonDocument()).FirstAsync();
        for (var offset = 1; offset < count; offset += 5_000)
        {
            var batch = new List<BsonDocument>();
            for (var i = offset; i < Math.Min(offset + 5_000, count); i++)
            {
                var document = template.DeepClone().AsBsonDocument;
                document["_id"] = ObjectId.GenerateNewId();
                document["attributes"].AsBsonDocument["simpleField"] = distinctValues ? $"v-{i:D6}" : $"v-{i % 50:D2}";
                batch.Add(document);
            }

            await collection.InsertManyAsync(batch);
        }
    }

    private static async Task<(BsonDocument Sort, List<BsonDocument> Stages)> PageAsync(ITenantContext tenant,
        string modelName, bool? idTiebreak = null, bool descending = false)
    {
        var repository = (TenantRepository)tenant.GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtEntity>(session, TypeId(modelName),
            RtEntityQueryOptions.Create().SortOrder(FieldName, descending ? SortOrders.Descending : SortOrders.Ascending));
        List<BsonDocument> stages;
        if (idTiebreak.HasValue)
        {
            stages = Repositories.Query.CountOnlyParityTests.Render<RtEntity, RtEntity>(
                query.CreatePagePipelineStageDefinitions(10, 10, idTiebreak.Value)).ToList();
        }
        else
        {
            // The page the query really runs (the gate decides the tiebreak).
            var executed = new List<SingleOriginQuery<OctoObjectId, RtEntity>.ExecutedPipeline>();
            query.PipelineObserver = executed.Add;
            await query.ExecuteQuery(session, 10, 10, includeTotalCount: false);
            stages = executed.Single(e => e.Purpose == "page").Stages.ToList();
        }

        var sort = stages.Single(s => s.GetElement(0).Name == "$sort")["$sort"].AsBsonDocument;
        return (sort, stages);
    }

    /// <summary>The winning plan of a pipeline: its index and whether it sorts in memory.</summary>
    private static async Task<(string? Index, bool HasSort)> ExplainAsync(IMongoCollection<BsonDocument> collection,
        List<BsonDocument> stages)
    {
        var explain = await collection.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            {
                "explain", new BsonDocument
                {
                    { "aggregate", collection.CollectionNamespace.CollectionName },
                    { "pipeline", new BsonArray(stages) },
                    { "cursor", new BsonDocument() }
                }
            },
            { "verbosity", "queryPlanner" }
        });

        var plan = FindFirst(explain, "winningPlan") ?? throw new InvalidOperationException(explain.ToJson());
        var stageNames = new List<string>();
        string? index = null;
        Walk(plan, (name, value) =>
        {
            if (name == "stage")
            {
                stageNames.Add(value.AsString);
            }
            else if (name == "indexName" && index == null)
            {
                index = value.AsString;
            }
        });
        return (index, stageNames.Any(s => s is "SORT" or "SORT_KEY_GENERATOR"));
    }

    private static BsonValue? FindFirst(BsonValue node, string name)
    {
        switch (node)
        {
            case BsonDocument document:
                foreach (var element in document)
                {
                    if (element.Name == name)
                    {
                        return element.Value;
                    }

                    if (FindFirst(element.Value, name) is { } found)
                    {
                        return found;
                    }
                }

                break;
            case BsonArray array:
                foreach (var element in array)
                {
                    if (FindFirst(element, name) is { } found)
                    {
                        return found;
                    }
                }

                break;
        }

        return null;
    }

    private static void Walk(BsonValue node, Action<string, BsonValue> visit)
    {
        switch (node)
        {
            case BsonDocument document:
                foreach (var element in document)
                {
                    visit(element.Name, element.Value);
                    Walk(element.Value, visit);
                }

                break;
            case BsonArray array:
                foreach (var element in array)
                {
                    Walk(element, visit);
                }

                break;
        }
    }
}
