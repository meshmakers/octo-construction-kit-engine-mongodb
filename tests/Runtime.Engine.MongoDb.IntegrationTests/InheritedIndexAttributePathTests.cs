using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#9524: index attribute paths resolve against the inheritance chain of the index-defining type. A base type
///     (not a collection root) assigns <c>Label</c> (String) and <c>Span</c> (Record with <c>From</c>); the collection
///     root <c>Process</c> and its descendant <c>Step</c> index them. Against real MongoDB: the plain inherited attribute
///     gets exactly the key the fallback built before (<c>attributes.label</c>, no rebuild on existing tenants), the
///     inherited record path the stored path (<c>attributes.span.attributes.from</c>), the index names keep the
///     defining type, and a second index update neither builds nor drops anything.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class InheritedIndexAttributePathTests(ImportTestCkModelFixture fixture)
{
    private const string RootTypeName = "Process";

    [Fact]
    public async Task IndexesOnInheritedAttributes_ResolveToTheStoredPaths_AndAreStableAcrossUpdates()
    {
        await WithTenantAsync(async (tenant, systemContext) =>
        {
            await tenant.ImportCkModelAsync(Model("InhIdx", "1.0.0"));
            var collection = Collection(tenant, "InhIdx");

            var indexes = await IndexesAsync(collection);

            // Plain inherited attribute: byte-identical to the fallback key ("attributes." + camelCase of the path).
            var label = Assert.Single(indexes, i => KeysOf(i).SequenceEqual(
                ["ckTypeId", "attributes.label", "_id", "rtState"]));
            Assert.Equal(IndexAttributePaths.FallbackPath("Label"), KeysOf(label)[1]);
            Assert.StartsWith(IndexBaseName("InhIdx", RootTypeName, 0), label["name"].AsString);

            // Inherited record path: the stored camelCase path per segment, not the fallback "attributes.span.From".
            var span = Assert.Single(indexes, i => KeysOf(i).SequenceEqual(
                ["ckTypeId", "attributes.span.attributes.from", "_id", "rtState"]));
            Assert.StartsWith(IndexBaseName("InhIdx", RootTypeName, 1), span["name"].AsString);

            // Descendant of the collection root: its chain runs through Process to the base type.
            var step = Assert.Single(indexes, i => KeysOf(i).SequenceEqual(
                ["ckTypeId", "attributes.label", "attributes.span.attributes.from", "_id", "rtState"]));
            Assert.StartsWith(IndexBaseName("InhIdx", "Step", 0), step["name"].AsString);

            Assert.DoesNotContain(indexes, i => KeysOf(i).Any(k => k == "attributes.span.From"));

            var afterImport = Describe(indexes);
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await tenant.UpdateIndexesAsync(adminSession);
                await tenant.UpdateIndexesAsync(adminSession);
            }

            Assert.Equal(afterImport, Describe(await IndexesAsync(collection)));
        });
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task WithTenantAsync(Func<ITenantContext, ISystemContext, Task> test)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"IA_{Guid.NewGuid():N}"[..20];
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
    ///     <c>Named</c> (base, not a collection root) assigns <c>Label</c> and <c>Span</c>; <c>Process</c> (collection
    ///     root) indexes <c>Label</c> and <c>Span.From</c>; <c>Step</c> (derived from Process) indexes both together.
    /// </summary>
    private static CkCompiledModelRoot Model(string modelName, string version)
    {
        var modelVersion = $"{modelName}-{version}";

        CkTypeAttributeDto Assign(string name) => new()
        {
            CkAttributeId = new CkId<CkAttributeId>($"{modelVersion}/{name}"), AttributeName = name, IsOptional = true
        };

        CkTypeIndexDto Index(params string[] paths) => new()
        {
            IndexType = IndexTypeDto.Ascending, Fields = [new() { AttributePaths = paths.ToList() }]
        };

        return new CkCompiledModelRoot
        {
            ModelId = new CkModelId(modelVersion),
            Dependencies = [SystemCkIds.CkModelId],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId("Label"), ValueType = AttributeValueTypesDto.String },
                new CkAttributeDto { AttributeId = new CkAttributeId("From"), ValueType = AttributeValueTypesDto.DateTime },
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId("Span"), ValueType = AttributeValueTypesDto.Record,
                    ValueCkRecordId = new CkId<CkRecordId>($"{modelVersion}/SpanRecord")
                }
            ],
            Records = [new CkRecordDto { RecordId = new CkRecordId("SpanRecord"), Attributes = [Assign("From")] }],
            Types =
            [
                new()
                {
                    TypeId = new CkTypeId("Named"),
                    DerivedFromCkTypeId = SystemCkIds.CkEntityTypeId,
                    Attributes = [Assign("Label"), Assign("Span")]
                },
                new()
                {
                    TypeId = new CkTypeId(RootTypeName),
                    DerivedFromCkTypeId = new CkId<CkTypeId>($"{modelVersion}/Named"),
                    IsCollectionRoot = true,
                    Attributes = [],
                    Indexes = [Index("Label"), Index("Span.From")]
                },
                new()
                {
                    TypeId = new CkTypeId("Step"),
                    DerivedFromCkTypeId = new CkId<CkTypeId>($"{modelVersion}/{RootTypeName}"),
                    Attributes = [],
                    Indexes = [Index("Label", "Span.From")]
                }
            ]
        };
    }

    private static string IndexBaseName(string modelName, string typeName, int slot) =>
        new RtCkId<CkTypeId>($"{modelName}/{typeName}").GetCkTypeCollectionName() + "_" + slot;

    private static IMongoCollection<BsonDocument> Collection(ITenantContext tenant, string modelName)
    {
        var repository = (TenantRepository)tenant.GetTenantRepository();
        var collection = repository.DataSource
            .GetRtDatabaseCollectionByTypeId<RtEntity>(new RtCkId<CkTypeId>($"{modelName}/{RootTypeName}"))
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
}
