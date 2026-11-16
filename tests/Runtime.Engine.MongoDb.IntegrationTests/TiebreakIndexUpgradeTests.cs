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
    private const string OtherFieldName = "OtherField";
    private const string OtherFieldPath = "attributes.otherField";
    private const string ChildTypeName = "ChildType";

    private static readonly string[] LegacyKeys = ["ckTypeId", FieldPath, "rtState"];
    private static readonly string[] TiebreakKeys = ["ckTypeId", FieldPath, "_id", "rtState"];
    private static readonly string[] OtherTiebreakKeys = ["ckTypeId", OtherFieldPath, "_id", "rtState"];

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

    /// <summary>
    ///     A deep sorted page (AB#9138 hint) over an engine-made legacy index keeps its hint to that index: the gate
    ///     leaves the tiebreak out for this sort, so the hint needs only the caller's key. After the upgrade the hint goes
    ///     to the tiebreak index, which provides the attribute and <c>_id</c>.
    /// </summary>
    [Fact]
    public async Task DeepPage_IsHintedToTheLegacyIndex_AndAfterTheUpgradeToTheTiebreakIndex()
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.0", IndexTypeDto.Ascending));
            var collection = Collection(tenant, "AscModel");
            await SeedAsync(tenant, "AscModel", 3_000);
            var legacyName = await MakeLegacyAsync(collection);

            var legacyPage = await DeepPageAsync(tenant, "AscModel");
            Assert.Equal(legacyName, legacyPage.Hint);
            Assert.Equal(new BsonDocument(FieldPath, 1),
                legacyPage.Stages.Single(s => s.GetElement(0).Name == "$sort")["$sort"].AsBsonDocument);

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
            }

            var tiebreakPage = await DeepPageAsync(tenant, "AscModel");
            Assert.Equal(legacyName + TiebreakIndexShape.NameSuffix, tiebreakPage.Hint);
            Assert.Equal(new BsonDocument { { FieldPath, 1 }, { "_id", 1 } },
                tiebreakPage.Stages.Single(s => s.GetElement(0).Name == "$sort")["$sort"].AsBsonDocument);
            Assert.False((await ExplainAsync(collection, tiebreakPage.Stages.ToList())).HasSort);
        });
    }

    private static async Task<SingleOriginQuery<OctoObjectId, RtEntity>.ExecutedPipeline> DeepPageAsync(
        ITenantContext tenant, string modelName)
    {
        var repository = (TenantRepository)tenant.GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        var query = await repository.CreateRtEntitiesByTypeQueryAsync<RtEntity>(session, TypeId(modelName),
            RtEntityQueryOptions.Create().SortOrder(FieldName, SortOrders.Ascending));
        var executed = new List<SingleOriginQuery<OctoObjectId, RtEntity>.ExecutedPipeline>();
        query.PipelineObserver = executed.Add;
        await query.ExecuteQuery(session, 1_500, 10, includeTotalCount: false);
        return executed.Single(e => e.Purpose == "page");
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
            var inBuild = 0;
            string? gap = null;
            do
            {
                var (indexes, building) = await FinishedIndexesAsync(collection);
                var onTheAttribute = indexes.Count(i => KeysOf(i).Take(2).SequenceEqual(["ckTypeId", FieldPath]));
                // Recorded, not asserted here: the upgrade must finish before the tenant is dropped.
                gap ??= onTheAttribute >= 1 ? null : $"observation {observations}: no finished index on {FieldPath}";
                sawBoth |= onTheAttribute == 2;
                inBuild += building.Any(i => KeysOf(i).SequenceEqual(TiebreakKeys)) ? 1 : 0;
                observations++;
            } while (!upgrade.IsCompleted);

            await upgrade;
            Assert.Null(gap);

            Assert.True(observations >= 2, $"only {observations} observation(s) during the upgrade");
            Assert.Single(await IndexesAsync(collection), i => KeysOf(i).SequenceEqual(TiebreakKeys));
            // Informative: whether the build was slow enough to see both indexes at once.
            RunProgress.Report($"AB#8960 gapless upgrade: observations {observations}, new index only in build: " +
                               $"{inBuild}, both finished indexes seen: {sawBoth}");
        });
    }

    /// <summary>
    ///     AC 13: a CK update that changes the fields of an Ascending index after the tiebreak upgrade. The new
    ///     definition's expected name (<c>&lt;Type&gt;_&lt;n&gt;_id</c>) is held by the old index with other keys; the new
    ///     index is built under <c>&lt;Type&gt;_&lt;n&gt;_id_&lt;hash&gt;</c> and the old one dropped after the build. At every
    ///     observation during the update the type has the old or the new index (the same method as the 60,000-entity
    ///     upgrade test). A second change of the same slot (from the hashed name to the next keys) is gapless too, and
    ///     a re-run after the updates changes nothing.
    /// </summary>
    [Fact]
    public async Task CkUpdate_ChangingTheFieldsOfATiebreakIndex_NeverLeavesTheTypeWithoutTheIndex()
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.0", IndexTypeDto.Ascending));
            var collection = Collection(tenant, "AscModel");
            await MakeLegacyAsync(collection);
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
            }

            var tiebreakName = Assert.Single(await IndexesAsync(collection),
                i => KeysOf(i).SequenceEqual(TiebreakKeys))["name"].AsString;
            await SeedAsync(tenant, "AscModel", 60_000);

            // First change: SimpleField -> OtherField. The expected name is taken by the old index.
            await ObserveGaplessAsync(collection, "first change",
                tenant.ImportCkModelAsync(Model("AscModel", "1.0.1", IndexTypeDto.Ascending, [OtherFieldName])),
                TiebreakKeys, OtherTiebreakKeys);
            var afterFirst = await IndexesAsync(collection);
            Assert.DoesNotContain(afterFirst, i => KeysOf(i).Contains(FieldPath));
            var hashedName = Assert.Single(afterFirst, i => KeysOf(i).SequenceEqual(OtherTiebreakKeys))["name"].AsString;
            Assert.Matches($"^{tiebreakName}_[0-9a-f]{{6}}$", hashedName);

            // Second change of the same slot: OtherField -> OtherField + SimpleField. The expected name is free again.
            string[] compoundKeys = ["ckTypeId", OtherFieldPath, FieldPath, "_id", "rtState"];
            await ObserveGaplessAsync(collection, "second change",
                tenant.ImportCkModelAsync(Model("AscModel", "1.0.2", IndexTypeDto.Ascending,
                    [OtherFieldName, FieldName])),
                OtherTiebreakKeys, compoundKeys);
            var afterSecond = await IndexesAsync(collection);
            Assert.DoesNotContain(afterSecond, i => i["name"].AsString == hashedName);
            var compound = Assert.Single(afterSecond, i => KeysOf(i).SequenceEqual(compoundKeys));
            Assert.Equal(tiebreakName, compound["name"].AsString);
            Assert.True(await GateAsync(collection, OtherFieldPath));

            // Idempotent: an index update, the upgrade and the same import again change nothing.
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpdateIndexesAsync(adminSession);
                await tenant.UpgradeTiebreakIndexesAsync(adminSession);
            }

            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.2", IndexTypeDto.Ascending,
                [OtherFieldName, FieldName]));
            Assert.Equal(Describe(afterSecond), Describe(await IndexesAsync(collection)));
        });
    }

    /// <summary>
    ///     AC 8: an index with the right definition under another name (e.g. built under the hashed name, or by another
    ///     slot) is kept as it is - no drop and rebuild under the expected name, which would leave the collection without
    ///     the index for the time of the build.
    /// </summary>
    [Fact]
    public async Task IndexWithTheDefinitionUnderAnotherName_IsKept_NotRebuilt()
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.0", IndexTypeDto.Ascending));
            var collection = Collection(tenant, "AscModel");
            var tiebreakName = Assert.Single(await IndexesAsync(collection),
                i => KeysOf(i).SequenceEqual(TiebreakKeys))["name"].AsString;
            const string otherName = "kept_by_another_name";
            await collection.Indexes.DropOneAsync(tiebreakName);
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ckTypeId", 1 }, { FieldPath, 1 }, { "_id", 1 }, { "rtState", 1 } },
                new CreateIndexOptions { Name = otherName }));
            var before = Describe(await IndexesAsync(collection));

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpdateIndexesAsync(adminSession);
            }

            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.1", IndexTypeDto.Ascending));
            Assert.Equal(before, Describe(await IndexesAsync(collection)));
        });
    }

    /// <summary>
    ///     AC 8: an index with the keys of a CK Ascending index but options the engine never sets (here a partial filter)
    ///     is not adopted: the engine builds its own under the expected name next to it and drops the other one after.
    /// </summary>
    [Fact]
    public async Task IndexWithTheKeysButForeignOptions_IsNotAdopted()
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.0", IndexTypeDto.Ascending));
            var collection = Collection(tenant, "AscModel");
            var tiebreakName = Assert.Single(await IndexesAsync(collection),
                i => KeysOf(i).SequenceEqual(TiebreakKeys))["name"].AsString;
            const string foreignName = "same_keys_partial";
            await collection.Indexes.DropOneAsync(tiebreakName);
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ckTypeId", 1 }, { FieldPath, 1 }, { "_id", 1 }, { "rtState", 1 } },
                new CreateIndexOptions<BsonDocument>
                {
                    Name = foreignName,
                    PartialFilterExpression = new BsonDocument("rtState", new BsonDocument("$exists", true))
                }));

            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpdateIndexesAsync(adminSession);
            }

            var indexes = await IndexesAsync(collection);
            Assert.DoesNotContain(indexes, i => i["name"].AsString == foreignName);
            var ours = Assert.Single(indexes, i => KeysOf(i).SequenceEqual(TiebreakKeys));
            Assert.Equal(tiebreakName, ours["name"].AsString);
            Assert.False(ours.Contains("partialFilterExpression"));
        });
    }

    /// <summary>
    ///     An index with our keys that differs only in an option the engine never sets (sparse) can block our build
    ///     (MongoDB refuses a second index on the same keys that differs only in such options). An index is dropped
    ///     only once its replacement is built: the other index stays, the import does not fail, and the attribute keeps
    ///     an index.
    /// </summary>
    [Fact]
    public async Task OurBuildRefusedBecauseOfASparseTwin_KeepsTheTwin()
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.0", IndexTypeDto.Ascending));
            var collection = Collection(tenant, "AscModel");
            var tiebreakName = Assert.Single(await IndexesAsync(collection),
                i => KeysOf(i).SequenceEqual(TiebreakKeys))["name"].AsString;
            const string twinName = "same_keys_sparse";
            await collection.Indexes.DropOneAsync(tiebreakName);
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ckTypeId", 1 }, { FieldPath, 1 }, { "_id", 1 }, { "rtState", 1 } },
                new CreateIndexOptions { Name = twinName, Sparse = true }));

            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.1", IndexTypeDto.Ascending));

            var indexes = await IndexesAsync(collection);
            Assert.Contains(indexes, i => KeysOf(i).SequenceEqual(TiebreakKeys));
            // Either MongoDB built ours next to the twin (then the twin was dropped after the build) or it refused
            // ours (then the twin stays).
            Assert.True(indexes.Any(i => i["name"].AsString == tiebreakName) ||
                        indexes.Any(i => i["name"].AsString == twinName));
            RunProgress.Report("AB#8960 sparse twin: " + string.Join(", ", Describe(indexes)));
        });
    }

    /// <summary>
    ///     Both unique index types carry a partial filter; only UniqueNotDeleted filters <c>rtState</c>. A Unique index is
    ///     read back as Unique, so a further import finds it as it is: no rebuild under another name, no failed state.
    /// </summary>
    [Theory]
    [InlineData(IndexTypeDto.Unique)]
    [InlineData(IndexTypeDto.UniqueNotDeleted)]
    public async Task UniqueIndex_IsFoundAsItIs_ByTheNextImport(IndexTypeDto indexType)
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("UniqModel", "1.0.0", indexType));
            var collection = Collection(tenant, "UniqModel");
            var before = Describe(await IndexesAsync(collection));

            await tenant.ImportCkModelAsync(Model("UniqModel", "1.0.1", indexType));

            Assert.Equal(before, Describe(await IndexesAsync(collection)));
            var repository = tenant.GetTenantRepository();
            var ckTypes = await repository.GetCkTypeAsync(repository.GetSession(),
                [new CkId<CkTypeId>($"UniqModel-1.0.1/{TypeName}")], RtEntityQueryOptions.Create());
            var states = Assert.Single(ckTypes.Items).IndexStates ?? [];
            Assert.DoesNotContain(states, s => s.State == IndexState.Failed);
        });
    }

    /// <summary>
    ///     A unique index filters its types. When a CK update adds a descendant of the defining type, the index with the
    ///     old type set is replaced by one that names the new type too (built next to it, the old one dropped after),
    ///     so the descendant's values are unique-checked; a further import keeps it.
    /// </summary>
    [Theory]
    [InlineData(IndexTypeDto.Unique)]
    [InlineData(IndexTypeDto.UniqueNotDeleted)]
    public async Task UniqueIndex_AfterADescendantIsAdded_FiltersTheNewTypeToo(IndexTypeDto indexType)
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("UniqModel", "1.0.0", indexType));
            var collection = Collection(tenant, "UniqModel");

            await tenant.ImportCkModelAsync(Model("UniqModel", "1.0.1", indexType, withChildType: true));

            var unique = Assert.Single(await IndexesAsync(collection), i => i.GetValue("unique", false).ToBoolean());
            Assert.Contains($"UniqModel/{ChildTypeName}", unique["partialFilterExpression"].ToJson());
            var after = Describe(await IndexesAsync(collection));
            await tenant.ImportCkModelAsync(Model("UniqModel", "1.0.2", indexType, withChildType: true));
            Assert.Equal(after, Describe(await IndexesAsync(collection)));
        });
    }

    /// <summary>
    ///     The unique index a type-set change replaces stays when the replacement cannot be built (here: the new
    ///     descendant already holds duplicate values), also when it is not under the expected name.
    /// </summary>
    [Fact]
    public async Task UniqueIndex_UnderAnotherName_StaysWhenItsReplacementFails()
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("UniqModel", "1.0.0", IndexTypeDto.Unique));
            var collection = Collection(tenant, "UniqModel");
            var unique = Assert.Single(await IndexesAsync(collection), i => i.GetValue("unique", false).ToBoolean());
            const string keptName = "unique_under_another_name";
            await collection.Indexes.DropOneAsync(unique["name"].AsString);
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(unique["key"].AsBsonDocument,
                new CreateIndexOptions<BsonDocument>
                {
                    Name = keptName, Unique = true,
                    PartialFilterExpression = unique["partialFilterExpression"].AsBsonDocument
                }));

            // Entities of the descendant-to-be with the same value: the replacement that covers them must fail.
            await SeedAsync(tenant, "UniqModel", 1, distinctValues: true);
            var template = await collection.Find(new BsonDocument()).FirstAsync();
            foreach (var _ in Enumerable.Range(0, 2))
            {
                var document = template.DeepClone().AsBsonDocument;
                document["_id"] = ObjectId.GenerateNewId();
                document["ckTypeId"] = $"UniqModel/{ChildTypeName}";
                document["attributes"].AsBsonDocument["simpleField"] = "duplicate";
                await collection.InsertOneAsync(document);
            }

            await tenant.ImportCkModelAsync(Model("UniqModel", "1.0.1", IndexTypeDto.Unique, withChildType: true));

            Assert.Contains(await IndexesAsync(collection), i => i["name"].AsString == keptName);
        });
    }

    /// <summary>
    ///     A text index kept under the name of an earlier slot (a regular index was added before it) is still the one a
    ///     change of its fields replaces: a collection holds only one text index.
    /// </summary>
    [Fact]
    public async Task TextIndex_KeptUnderAnEarlierSlotName_IsReplacedWhenItsFieldsChange()
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("TextModel", "1.0.0", IndexTypeDto.Text));
            var collection = Collection(tenant, "TextModel");
            var textName = Assert.Single(await IndexesAsync(collection), i => KeysOf(i).Contains("_fts"))["name"]
                .AsString;

            // An Ascending index before it: the text index moves to slot 1 and is kept under its slot-0 name.
            await tenant.ImportCkModelAsync(Model("TextModel", "1.0.1", IndexTypeDto.None, indexes:
            [
                Index(IndexTypeDto.Ascending, Fields(OtherFieldName)),
                Index(IndexTypeDto.Text, new CkIndexFieldsDto { AttributePaths = [FieldName], Weight = 1 })
            ]));
            Assert.Equal(textName,
                Assert.Single(await IndexesAsync(collection), i => KeysOf(i).Contains("_fts"))["name"].AsString);

            // Now its fields change.
            await tenant.ImportCkModelAsync(Model("TextModel", "1.0.2", IndexTypeDto.None, indexes:
            [
                Index(IndexTypeDto.Ascending, Fields(OtherFieldName)),
                Index(IndexTypeDto.Text, new CkIndexFieldsDto { AttributePaths = [OtherFieldName], Weight = 1 })
            ]));
            var text = Assert.Single(await IndexesAsync(collection), i => KeysOf(i).Contains("_fts"));
            Assert.Equal([OtherFieldPath], text["weights"].AsBsonDocument.Names);
        });
    }

    /// <summary>
    ///     An Ascending definition with several field groups is built as one key list and read back by
    ///     <c>listIndexes</c> as one group; it still matches its definition, so a further import neither keeps a
    ///     legacy index wrongly nor rebuilds it.
    /// </summary>
    [Fact]
    public async Task AscendingIndexWithSeveralFieldGroups_IsFoundByTheNextImport()
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            List<CkTypeIndexDto> indexes = [Index(IndexTypeDto.Ascending, Fields(FieldName), Fields(OtherFieldName))];
            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.0", IndexTypeDto.None, indexes: indexes));
            var collection = Collection(tenant, "AscModel");
            var before = await IndexesAsync(collection);
            Assert.Single(before,
                i => KeysOf(i).SequenceEqual(["ckTypeId", FieldPath, OtherFieldPath, "_id", "rtState"]));

            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.1", IndexTypeDto.None, indexes: indexes));
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpdateIndexesAsync(adminSession);
            }

            Assert.Equal(Describe(before), Describe(await IndexesAsync(collection)));
        });
    }

    /// <summary>
    ///     AB#8960 gate: a legacy index leaves the tiebreak out only when it provides the complete caller sort. Sorted by
    ///     (Name, Rank) over a legacy index on Name alone, the page sorts in memory anyway, so it gets the tiebreak and a
    ///     total order.
    /// </summary>
    [Fact]
    public async Task Gate_SortByMoreKeysThanTheLegacyIndexProvides_GetsTheTiebreak()
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.0", IndexTypeDto.Ascending));
            var collection = Collection(tenant, "AscModel");
            await MakeLegacyAsync(collection);

            Assert.False(await PagingTiebreakGate.IsEnabledAsync(collection, [(FieldPath, false)]));
            Assert.False(await PagingTiebreakGate.IsEnabledAsync(collection, [(FieldPath, true)]));
            Assert.True(await PagingTiebreakGate.IsEnabledAsync(collection,
                [(FieldPath, false), (OtherFieldPath, false)]));
        });
    }

    /// <summary>
    ///     AC 8: when the build of the changed index fails (here: a compound index over two array fields of one document,
    ///     which MongoDB refuses), the old index with the expected name stays, and the update goes on.
    /// </summary>
    [Fact]
    public async Task CkUpdate_FailedBuildOfTheChangedIndex_KeepsTheOldIndex()
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.0", IndexTypeDto.Ascending));
            var collection = Collection(tenant, "AscModel");
            await SeedAsync(tenant, "AscModel", 10);
            var template = await collection.Find(new BsonDocument()).FirstAsync();
            template["_id"] = ObjectId.GenerateNewId();
            template["attributes"].AsBsonDocument["simpleField"] = new BsonArray { "a", "b" };
            template["attributes"].AsBsonDocument["otherField"] = new BsonArray { "c", "d" };
            await collection.InsertOneAsync(template);
            var before = Describe(await IndexesAsync(collection));

            await tenant.ImportCkModelAsync(Model("AscModel", "1.0.1", IndexTypeDto.Ascending,
                [FieldName, OtherFieldName]));

            Assert.Equal(before, Describe(await IndexesAsync(collection)));
        });
    }

    /// <summary>
    ///     A collection holds at most one text index, so a changed text index cannot be built next to the old one: it is
    ///     still recreated (dropped, then built) under its name.
    /// </summary>
    [Fact]
    public async Task CkUpdate_ChangingTheFieldsOfATextIndex_RecreatesIt()
    {
        await WithTenantAsync(async tenant =>
        {
            await tenant.ImportCkModelAsync(Model("TextModel", "1.0.0", IndexTypeDto.Text));
            var collection = Collection(tenant, "TextModel");
            var before = Assert.Single(await IndexesAsync(collection), i => KeysOf(i).Contains("_fts"));
            Assert.Contains(FieldPath, before["weights"].AsBsonDocument.Names);

            await tenant.ImportCkModelAsync(Model("TextModel", "1.0.1", IndexTypeDto.Text, [OtherFieldName]));

            var after = Assert.Single(await IndexesAsync(collection), i => KeysOf(i).Contains("_fts"));
            Assert.Equal(before["name"].AsString, after["name"].AsString);
            Assert.Equal([OtherFieldPath], after["weights"].AsBsonDocument.Names);
        });
    }

    /// <summary>
    ///     Runs <paramref name="update" /> and lists the indexes until it is done: at every observation the old index
    ///     (<paramref name="oldKeys" />) or the new one (<paramref name="newKeys" />) exists. A gap is recorded, not asserted
    ///     in the loop: the update must finish before the tenant is dropped.
    /// </summary>
    private static async Task ObserveGaplessAsync(IMongoCollection<BsonDocument> collection, string step, Task update,
        string[] oldKeys, string[] newKeys)
    {
        var observations = 0;
        var sawBoth = false;
        var inBuild = 0;
        string? gap = null;
        do
        {
            var (indexes, building) = await FinishedIndexesAsync(collection);
            inBuild += building.Any(i => KeysOf(i).SequenceEqual(newKeys)) ? 1 : 0;
            var onOld = indexes.Any(i => KeysOf(i).SequenceEqual(oldKeys));
            var onNew = indexes.Any(i => KeysOf(i).SequenceEqual(newKeys));
            gap ??= onOld || onNew
                ? null
                : $"{step}, observation {observations}: neither the old nor the new index: " +
                  string.Join(", ", Describe(indexes));
            sawBoth |= onOld && onNew;
            observations++;
        } while (!update.IsCompleted);

        await update;
        Assert.Null(gap);
        Assert.True(observations >= 2, $"{step}: only {observations} observation(s) during the update");
        Assert.Contains(await IndexesAsync(collection), i => KeysOf(i).SequenceEqual(newKeys));
        RunProgress.Report($"AB#8960 AC 13 {step}: observations {observations}, new index only in build: {inBuild}, " +
                           $"both finished indexes seen: {sawBoth}");
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
    ///     at most <see cref="CollectionIndexCache.Ttl" /> of the regression. Expiry is simulated by
    ///     <see cref="CollectionIndexCache.Invalidate{TDocument}" />, which an index update of the same process also calls.
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
            Assert.False(await PagingTiebreakGate.IsEnabledAsync(collection, [(FieldPath, false)]));
            Assert.True(await GateAsync(collection));

            // Another instance rolls back: the legacy index is back - this process keeps the tiebreak until expiry.
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ckTypeId", 1 }, { FieldPath, 1 }, { "rtState", 1 } },
                new CreateIndexOptions { Name = legacyName }));
            Assert.True(await PagingTiebreakGate.IsEnabledAsync(collection, [(FieldPath, false)]));
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

    private static CkCompiledModelRoot Model(string modelName, string version, IndexTypeDto indexType,
        string[]? indexedFields = null, List<CkTypeIndexDto>? indexes = null, bool withChildType = false)
    {
        var modelVersion = $"{modelName}-{version}";
        return new CkCompiledModelRoot
        {
            ModelId = new CkModelId(modelVersion),
            Dependencies = [SystemCkIds.CkModelId],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId(FieldName), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId(OtherFieldName), ValueType = AttributeValueTypesDto.String
                }
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
                        },
                        new()
                        {
                            CkAttributeId = new CkId<CkAttributeId>($"{modelVersion}/{OtherFieldName}"),
                            AttributeName = OtherFieldName
                        }
                    ],
                    Indexes = indexes ?? (indexType == IndexTypeDto.None
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
                                    AttributePaths = indexedFields?.ToList() ?? [FieldName],
                                    Weight = indexType == IndexTypeDto.Text ? 1 : null
                                }
                            ]
                        }
                    ])
                },
                .. withChildType
                    ? new List<CkCompiledTypeDto>
                    {
                        new()
                        {
                            TypeId = new CkTypeId(ChildTypeName),
                            DerivedFromCkTypeId = new CkId<CkTypeId>($"{modelVersion}/{TypeName}"),
                            Attributes = []
                        }
                    }
                    : []
            ]
        };
    }

    private static CkTypeIndexDto Index(IndexTypeDto indexType, params List<CkIndexFieldsDto> fields) =>
        new() { IndexType = indexType, Fields = fields };

    private static CkIndexFieldsDto Fields(params string[] attributePaths) =>
        new() { AttributePaths = attributePaths.ToList() };

    private static RtCkId<CkTypeId> TypeId(string modelName) => new($"{modelName}/{TypeName}");

    private static IMongoCollection<BsonDocument> Collection(ITenantContext tenant, string modelName)
    {
        var repository = (TenantRepository)tenant.GetTenantRepository();
        var collection = repository.DataSource.GetRtDatabaseCollectionByTypeId<RtEntity>(TypeId(modelName))
            .GetMongoCollection();
        return collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
    }

    /// <summary>
    ///     The indexes a query can use. <c>listIndexes</c> lists an index build in progress too; with
    ///     <c>includeBuildUUIDs</c> such an entry is <c>{ spec, buildUUID }</c> and is left out, so it never counts as
    ///     present in a gapless check.
    /// </summary>
    private static async Task<(List<BsonDocument> Finished, List<BsonDocument> InBuild)> FinishedIndexesAsync(
        IMongoCollection<BsonDocument> collection)
    {
        var result = await collection.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "listIndexes", collection.CollectionNamespace.CollectionName },
            { "includeBuildUUIDs", true },
            { "cursor", new BsonDocument("batchSize", 1000) }
        });
        var all = result["cursor"]["firstBatch"].AsBsonArray.Select(i => i.AsBsonDocument).ToList();
        return (all.Where(i => !i.Contains("buildUUID")).ToList(),
            all.Where(i => i.Contains("buildUUID")).Select(i => i["spec"].AsBsonDocument).ToList());
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
        CollectionIndexCache.Invalidate(collection);
        return await PagingTiebreakGate.IsEnabledAsync(collection, sortKey == null ? [] : [(sortKey, false)]);
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
        CollectionIndexCache.Invalidate(collection);
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
        entity.SetAttributeValue(OtherFieldName, AttributeValueTypesDto.String, "o-00");
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
                document["attributes"].AsBsonDocument["otherField"] = $"o-{i % 70:D2}";
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
