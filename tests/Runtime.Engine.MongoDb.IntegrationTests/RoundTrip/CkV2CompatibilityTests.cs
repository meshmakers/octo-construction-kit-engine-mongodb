using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CkModelImportGuard;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RoundTrip;

/// <summary>
///     CK v2 F1.3-S4 (AB#5917), real MongoDB, throwaway tenants:
///     <list type="bullet">
///         <item>Hidden backstop in index creation (review N6): a Hidden attribute is never indexed, also when the
///         index path only matches case-insensitively and the compiler's message 106 was bypassed.</item>
///         <item>The new collections are created lazily: a tenant without them loads and imports v1 and v2 models.</item>
///         <item>Rollback safety: a v1 import through the Phase 1 engine writes byte-identical <c>Ck*</c> documents to
///         the main engine (golden captured from engine-mongodb origin/main e816cd3).</item>
///     </list>
/// </summary>
[Collection(CkModelImportGuardCollection.Name)]
public class CkV2CompatibilityTests(CkModelImportGuardFixture fixture)
{
    [Fact]
    public async Task HiddenAttribute_IsNeverIndexed_EvenThroughACaseInsensitivePath()
    {
        await WithTenantAsync("hiddenidx", async (tenant, tenantId) =>
        {
            var systemId = await InstalledSystemAsync(tenantId);
            var id = new CkModelId("HiddenIdx-1.0.0");
            await tenant.ImportCkModelAsync(new CkCompiledModelRoot
            {
                ModelId = id,
                CkLanguage = 2,
                MinEngineVersion = "3.4.0",
                Dependencies = [systemId],
                Attributes =
                [
                    new CkAttributeDto { AttributeId = new CkAttributeId("PasswordHash-1"), ValueType = AttributeValueTypesDto.String },
                    new CkAttributeDto { AttributeId = new CkAttributeId("Code-1"), ValueType = AttributeValueTypesDto.String }
                ],
                Types =
                [
                    new CkCompiledTypeDto
                    {
                        TypeId = new CkTypeId("Account-1"), IsCollectionRoot = true,
                        DerivedFromCkTypeId = new CkId<CkTypeId>(systemId, new CkTypeId("Entity-1")),
                        Attributes =
                        [
                            new CkTypeAttributeDto
                            {
                                CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("PasswordHash-1")),
                                AttributeName = "PasswordHash", IsOptional = true, Access = CkAttributeAccessDto.Hidden
                            },
                            new CkTypeAttributeDto
                            {
                                CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("Code-1")),
                                AttributeName = "Code", IsOptional = true
                            }
                        ]
                    }
                ]
            });

            // A hand-edited model (or one written by an older compiler) that the compiler's message 106 never saw:
            // a text index on the lower-case "passwordHash" and an ascending index on Code, set on the stored type.
            var database = GetTenantDatabase(tenantId);
            await database.GetCollection<BsonDocument>("CkType").UpdateOneAsync(
                new BsonDocument("_id", $"{id.FullName}/Account-1"),
                new BsonDocument("$set", new BsonDocument("indexes", new BsonArray
                {
                    new BsonDocument { { "indexType", 2 }, { "fields", new BsonArray { new BsonDocument("attributeNames", new BsonArray { "passwordHash" }) } } },
                    new BsonDocument { { "indexType", 1 }, { "fields", new BsonArray { new BsonDocument("attributeNames", new BsonArray { "code" }) } } }
                })), cancellationToken: TestContext.Current.CancellationToken);

            using (var session = await fixture.GetSystemContext().GetAdminSessionAsync())
            {
                session.StartTransaction();
                await tenant.UpdateIndexesAsync(session);
                await session.CommitTransactionAsync();
            }

            var collection = (await (await database.ListCollectionNamesAsync(
                    cancellationToken: TestContext.Current.CancellationToken))
                .ToListAsync(TestContext.Current.CancellationToken)).Single(n => n.Contains("Account", StringComparison.Ordinal));
            var indexes = await (await database.GetCollection<BsonDocument>(collection).Indexes
                .ListAsync(TestContext.Current.CancellationToken)).ToListAsync(TestContext.Current.CancellationToken);
            var keys = indexes.SelectMany(i => i["key"].AsBsonDocument.Names).ToList();

            Assert.Contains(keys, k => k.Contains("code", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(keys, k => k.Contains("passwordHash", StringComparison.OrdinalIgnoreCase) ||
                                             k == "_fts" && indexes.Any(i => i.Contains("weights") &&
                                                 i["weights"].AsBsonDocument.Names.Any(w =>
                                                     w.Contains("passwordHash", StringComparison.OrdinalIgnoreCase))));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Error, "Skipping Hidden attribute 'passwordHash'"));
        });
    }

    /// <summary>
    ///     Review M-M1 (G3): all types of one collection share the attribute fields, so the backstop must consider
    ///     every type stored there — (a) a derived type's index over a Hidden attribute it inherits from the root and
    ///     (b) a sibling's Hidden assignment of a field another type indexes (also through the merged text index).
    ///     Models are hand-edited after the import, as a forged or older compiled model would be.
    /// </summary>
    [Fact]
    public async Task HiddenAttributeOfAnotherTypeInTheCollection_IsNeverIndexed()
    {
        await WithTenantAsync("hiddensib", async (tenant, tenantId) =>
        {
            var systemId = await InstalledSystemAsync(tenantId);
            var id = new CkModelId("HiddenSib-1.0.0");
            CkTypeAttributeDto Assign(string name, CkAttributeAccessDto? access = null) => new()
            {
                CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId($"{name}-1")), AttributeName = name,
                IsOptional = true, Access = access
            };
            await tenant.ImportCkModelAsync(new CkCompiledModelRoot
            {
                ModelId = id,
                CkLanguage = 2,
                MinEngineVersion = "3.4.0",
                Dependencies = [systemId],
                Attributes =
                [
                    new CkAttributeDto { AttributeId = new CkAttributeId("Pin-1"), ValueType = AttributeValueTypesDto.String },
                    new CkAttributeDto { AttributeId = new CkAttributeId("Code-1"), ValueType = AttributeValueTypesDto.String },
                    new CkAttributeDto { AttributeId = new CkAttributeId("Label-1"), ValueType = AttributeValueTypesDto.String }
                ],
                Types =
                [
                    new CkCompiledTypeDto
                    {
                        TypeId = new CkTypeId("Root-1"), IsCollectionRoot = true, Derivable = CkDerivableDto.Any,
                        DerivedFromCkTypeId = new CkId<CkTypeId>(systemId, new CkTypeId("Entity-1")),
                        Attributes = [Assign("Pin", CkAttributeAccessDto.Hidden)]
                    },
                    new CkCompiledTypeDto
                    {
                        TypeId = new CkTypeId("Left-1"),
                        DerivedFromCkTypeId = new CkId<CkTypeId>(id, new CkTypeId("Root-1")),
                        Attributes = [Assign("Code"), Assign("Label")]
                    },
                    new CkCompiledTypeDto
                    {
                        TypeId = new CkTypeId("Right-1"),
                        DerivedFromCkTypeId = new CkId<CkTypeId>(id, new CkTypeId("Root-1"))
                    }
                ]
            });

            var database = GetTenantDatabase(tenantId);
            var types = database.GetCollection<BsonDocument>("CkType");
            // (a) Left indexes the Pin it inherits from Root (Hidden there), (b) a text index over Code, which the
            // sibling Right assigns Hidden; Label is a control that must stay indexed.
            await types.UpdateOneAsync(new BsonDocument("_id", $"{id.FullName}/Left-1"),
                new BsonDocument("$set", new BsonDocument("indexes", new BsonArray
                {
                    new BsonDocument { { "indexType", 1 }, { "fields", new BsonArray { new BsonDocument("attributeNames", new BsonArray { "pin" }) } } },
                    new BsonDocument { { "indexType", 2 }, { "fields", new BsonArray { new BsonDocument("attributeNames", new BsonArray { "code", "label" }) } } }
                })), cancellationToken: TestContext.Current.CancellationToken);
            await types.UpdateOneAsync(new BsonDocument("_id", $"{id.FullName}/Right-1"),
                new BsonDocument("$set", new BsonDocument("attributes", new BsonArray
                {
                    new BsonDocument { { "_id", $"{id.FullName}/Code-1" }, { "attributeName", "Code" }, { "isOptional", true }, { "access", 3 } }
                })), cancellationToken: TestContext.Current.CancellationToken);

            using (var session = await fixture.GetSystemContext().GetAdminSessionAsync())
            {
                session.StartTransaction();
                await tenant.UpdateIndexesAsync(session);
                await session.CommitTransactionAsync();
            }

            var collection = (await (await database.ListCollectionNamesAsync(
                    cancellationToken: TestContext.Current.CancellationToken))
                .ToListAsync(TestContext.Current.CancellationToken)).Single(n => n.Contains("HiddenSib", StringComparison.Ordinal));
            var indexes = await (await database.GetCollection<BsonDocument>(collection).Indexes
                .ListAsync(TestContext.Current.CancellationToken)).ToListAsync(TestContext.Current.CancellationToken);
            var indexedFields = indexes.SelectMany(i => i["key"].AsBsonDocument.Names)
                .Concat(indexes.Where(i => i.Contains("weights")).SelectMany(i => i["weights"].AsBsonDocument.Names))
                .ToList();

            Assert.Contains(indexedFields, f => f.Contains("label", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(indexedFields, f => f.Contains("pin", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(indexedFields, f => f.Contains("code", StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Error, "Skipping Hidden attribute 'pin'"));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Error, "Skipping Hidden attribute 'code'"));
        });
    }

    /// <summary>
    ///     Review M-M2 (G3), message 126: a model that needs a newer engine is refused before anything is deleted —
    ///     the installed version of the same model keeps its row and stays Available.
    /// </summary>
    [Fact]
    public async Task ModelRequiringANewerEngine_IsRefusedBeforeTheInstalledVersionIsTouched()
    {
        await WithTenantAsync("minengine", async (tenant, tenantId) =>
        {
            var systemId = await InstalledSystemAsync(tenantId);
            CkCompiledModelRoot Model(string modelId, string? minEngineVersion) => new()
            {
                ModelId = new CkModelId(modelId),
                CkLanguage = 2,
                MinEngineVersion = minEngineVersion,
                Dependencies = [systemId],
                Attributes = [new CkAttributeDto { AttributeId = new CkAttributeId("Code-1"), ValueType = AttributeValueTypesDto.String }]
            };
            await tenant.ImportCkModelAsync(Model("MinEngine-1.0.0", "3.4.0"));

            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                tenant.ImportCkModelAsync(Model("MinEngine-2.0.0", "1000.0.0")));
            Assert.Contains("CkModelRequiresNewerEngine", Flatten(exception));

            var rows = await GetTenantDatabase(tenantId).GetCollection<BsonDocument>("CkModel")
                .Find(new BsonDocument("modelId", "MinEngine")).ToListAsync(TestContext.Current.CancellationToken);
            var row = Assert.Single(rows);
            Assert.Equal("MinEngine-1.0.0", row["_id"].AsString);
            Assert.Equal(1, row["modelState"].AsInt32);
        });
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var e = exception; e != null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        return string.Join(" | ", messages);
    }

    [Fact]
    public async Task TenantWithoutTheNewCollections_LoadsAndImportsV1ThenV2()
    {
        await WithTenantAsync("lazycoll", async (tenant, tenantId) =>
        {
            var database = GetTenantDatabase(tenantId);
            await database.DropCollectionAsync("CkInterface", TestContext.Current.CancellationToken);
            await database.DropCollectionAsync("CkTypeInterfaceImplementation", TestContext.Current.CancellationToken);

            // Reads on a tenant without the collections: the cache loads.
            await tenant.LoadCacheForTenantAsync();

            var result = new OperationResult();
            await tenant.ImportCkModelAsync(new CkModelId("Test-1.0.0"), result);
            Assert.False(result.HasErrors);

            await tenant.ImportCkModelAsync(CkV2KitchenSinkModel.Build(await InstalledSystemAsync(tenantId)));

            Assert.True(await database.GetCollection<BsonDocument>("CkInterface")
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                    cancellationToken: TestContext.Current.CancellationToken) > 0);
            Assert.True(await database.GetCollection<BsonDocument>("CkTypeInterfaceImplementation")
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                    cancellationToken: TestContext.Current.CancellationToken) > 0);
        });
    }

    /// <summary>
    ///     The golden file holds the <c>Ck*</c> documents engine-mongodb origin/main (e816cd3, pre-S3) wrote for a
    ///     fresh tenant plus a <c>Test-1.0.0</c> import (System and Test models). The Phase 1 engine must write exactly
    ///     the same documents — then a rollback to the main engine reads them unchanged. Regenerate only when the
    ///     System or Test model itself changes (both lines must change together in that case).
    /// </summary>
    [Fact]
    public async Task V1Import_WritesTheSameCkDocumentsAsTheMainEngine()
    {
        await WithTenantAsync("golden", async (tenant, tenantId) =>
        {
            var result = new OperationResult();
            await tenant.ImportCkModelAsync(new CkModelId("Test-1.0.0"), result);
            Assert.False(result.HasErrors);

            var database = GetTenantDatabase(tenantId);
            var models = (await database.GetCollection<BsonDocument>("CkModel").Find(FilterDefinition<BsonDocument>.Empty)
                .ToListAsync(TestContext.Current.CancellationToken)).Select(d => d["_id"].AsString).ToList();
            var actual = await CkDocumentSnapshot.TakeAsync(database, models, TestContext.Current.CancellationToken);
            var golden = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "testData", "ckv2-golden-main-v1-ck-documents.txt"),
                TestContext.Current.CancellationToken);

            var expectedLines = golden.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(CkDocumentSnapshot.NormalizeLine).ToList();
            var actualLines = actual.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var missing = expectedLines.Except(actualLines).ToList();
            var extra = actualLines.Except(expectedLines).ToList();
            Assert.True(missing.Count == 0 && extra.Count == 0,
                $"v1 Ck* documents differ from the main engine:\nonly main:\n{string.Join("\n", missing.Take(5))}\n" +
                $"only Phase 1:\n{string.Join("\n", extra.Take(5))}");
        });
    }

    private async Task<CkModelId> InstalledSystemAsync(string tenantId)
    {
        var model = await GetTenantDatabase(tenantId).GetCollection<BsonDocument>("CkModel")
            .Find(new BsonDocument("modelId", "System")).SingleAsync(TestContext.Current.CancellationToken);
        return new CkModelId(model["_id"].AsString);
    }

    private async Task WithTenantAsync(string prefix, Func<ITenantContext, string, Task> body)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"{prefix}{Guid.NewGuid():N}"[..20];
        try
        {
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }

            ITenantContext tenant;
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                tenant = await systemContext.GetChildTenantContextAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }

            await body(tenant, tenantId);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    private IMongoDatabase GetTenantDatabase(string tenantId)
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
        return new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(tenantId.ToLowerInvariant());
    }
}
