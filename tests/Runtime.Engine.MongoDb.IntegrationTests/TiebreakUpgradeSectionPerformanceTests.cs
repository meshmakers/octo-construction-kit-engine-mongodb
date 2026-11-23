using System.Diagnostics;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
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
///     AB#9302: the worst case a CK import waits for during the tiebreak upgrade is one collection's lock section. This
///     measures the section of one large collection (legacy index rebuilt in the tiebreak shape) and reports the
///     duration and the rate per million documents (<c>OCTO_TIEBREAK_SECTION_DOCS</c>, default 1,000,000 documents of
///     about 1 KB). Run on demand or in the daily performance pipeline.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
[Trait("Category", "Performance")]
[Trait("Category", "LoadSensitive")]
public class TiebreakUpgradeSectionPerformanceTests(ImportTestCkModelFixture fixture)
{
    private const string ModelName = "SectionPerf";
    private const string TypeName = "Big";
    private const string FieldName = "SimpleField";
    private const string FieldPath = "attributes.simpleField";
    private static readonly string[] TiebreakKeys = ["ckTypeId", FieldPath, "_id", "rtState"];

    [Fact(Timeout = 1_800_000)]
    public async Task OneCollectionSection_WorstCaseAnImportWaitsFor()
    {
        var ct = TestContext.Current.CancellationToken;
        var documents = int.TryParse(Environment.GetEnvironmentVariable("OCTO_TIEBREAK_SECTION_DOCS"), out var n) && n > 0
            ? n
            : 1_000_000;
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"TP_{Guid.NewGuid():N}"[..20];
        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            adminSession.StartTransaction();
            await systemContext.CreateChildTenantAsync(adminSession, tenantId, tenantId);
            await adminSession.CommitTransactionAsync();
        }

        try
        {
            var tenant = await systemContext.GetChildTenantContextAsync(tenantId);
            await tenant.ImportCkModelAsync(Model());
            var collection = Collection(tenant);
            await SeedAsync(tenant, collection, documents, ct);

            var tiebreak = Assert.Single(await IndexesAsync(collection), i => KeysOf(i).SequenceEqual(TiebreakKeys));
            var tiebreakName = tiebreak["name"].AsString;
            await collection.Indexes.DropOneAsync(tiebreakName, ct);
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ckTypeId", 1 }, { FieldPath, 1 }, { "rtState", 1 } },
                new CreateIndexOptions { Name = tiebreakName[..^TiebreakIndexShape.NameSuffix.Length] }),
                cancellationToken: ct);
            CollectionIndexCache.Invalidate(collection);

            var dataSource = (MongoDbRepositoryDataSource)((TenantRepository)tenant.GetTenantRepository()).DataSource;
            var ours = new RtCkId<CkTypeId>($"{ModelName}/{TypeName}").GetCkTypeCollectionName();
            var section = new Stopwatch();
            var others = TimeSpan.Zero;
            var otherStopwatch = new Stopwatch();
            dataSource.UpgradeSectionObserver = e =>
            {
                var isOurs = e.CollectionName == ours;
                if (e.Stage == IndexUpgradeSectionStage.Acquired)
                {
                    (isOurs ? section : otherStopwatch).Restart();
                }
                else if (e.Stage == IndexUpgradeSectionStage.Releasing)
                {
                    if (isOurs)
                    {
                        section.Stop();
                    }
                    else
                    {
                        otherStopwatch.Stop();
                        others = otherStopwatch.Elapsed > others ? otherStopwatch.Elapsed : others;
                    }
                }

                return Task.CompletedTask;
            };

            var run = Stopwatch.StartNew();
            using (var adminSession = await systemContext.GetAdminSessionAsync())
            {
                await dataSource.UpgradeTiebreakIndexesAsync(adminSession, ct);
            }

            run.Stop();
            Assert.Single(await IndexesAsync(collection), i => KeysOf(i).SequenceEqual(TiebreakKeys));
            Assert.True(section.Elapsed > TimeSpan.Zero);
            var perMillion = section.Elapsed.TotalSeconds * 1_000_000 / documents;
            RunProgress.Report(
                $"AB#9302 one-collection section: {documents:N0} documents in {section.Elapsed.TotalSeconds:F1} s " +
                $"({perMillion:F1} s per million); longest other section {others.TotalMilliseconds:F0} ms; whole " +
                $"upgrade {run.Elapsed.TotalSeconds:F1} s");
        }
        finally
        {
            using var cleanupSession = await systemContext.GetAdminSessionAsync();
            cleanupSession.StartTransaction();
            await systemContext.DropChildTenantAsync(cleanupSession, tenantId);
            await cleanupSession.CommitTransactionAsync();
        }
    }

    private static async Task SeedAsync(ITenantContext tenant, IMongoCollection<BsonDocument> collection, int count,
        CancellationToken ct)
    {
        var repository = tenant.GetTenantRepository();
        var entity = await repository.CreateTransientRtEntityAsync(
            new CkId<CkTypeId>(new CkModelId($"{ModelName}-1.0.0"), new CkTypeId(TypeName)));
        entity.RtId = OctoObjectId.GenerateNewId();
        entity.SetAttributeValue(FieldName, AttributeValueTypesDto.String, "v-00");
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            await repository.InsertOneRtEntityAsync(session, new RtCkId<CkTypeId>($"{ModelName}/{TypeName}"), entity);
            await session.CommitTransactionAsync();
        }

        var template = await collection.Find(new BsonDocument()).FirstAsync(ct);
        template["attributes"].AsBsonDocument["padding"] = new string('x', 900);
        for (var offset = 1; offset < count; offset += 10_000)
        {
            var batch = new List<BsonDocument>();
            for (var i = offset; i < Math.Min(offset + 10_000, count); i++)
            {
                var document = template.DeepClone().AsBsonDocument;
                document["_id"] = ObjectId.GenerateNewId();
                document["attributes"].AsBsonDocument["simpleField"] = $"v-{i % 1000:D3}";
                batch.Add(document);
            }

            await collection.InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false }, ct);
        }
    }

    private static CkCompiledModelRoot Model()
    {
        var modelVersion = $"{ModelName}-1.0.0";
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
                    Indexes =
                    [
                        new() { IndexType = IndexTypeDto.Ascending, Fields = [new() { AttributePaths = [FieldName] }] }
                    ]
                }
            ]
        };
    }

    private static IMongoCollection<BsonDocument> Collection(ITenantContext tenant)
    {
        var repository = (TenantRepository)tenant.GetTenantRepository();
        var collection = repository.DataSource
            .GetRtDatabaseCollectionByTypeId<RtEntity>(new RtCkId<CkTypeId>($"{ModelName}/{TypeName}"))
            .GetMongoCollection();
        return collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
    }

    private static async Task<List<BsonDocument>> IndexesAsync(IMongoCollection<BsonDocument> collection)
    {
        using var cursor = await collection.Indexes.ListAsync();
        return await cursor.ToListAsync();
    }

    private static string[] KeysOf(BsonDocument index) => index["key"].AsBsonDocument.Names.ToArray();
}
