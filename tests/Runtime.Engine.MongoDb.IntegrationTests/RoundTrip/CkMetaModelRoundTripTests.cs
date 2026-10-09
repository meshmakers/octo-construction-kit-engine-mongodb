using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RoundTrip;

/// <summary>
///     CK v2 Phase 0 hard gate (AB#5667 / AB#5668 / AB#5669, concept §4.6): every model is imported into a
///     throwaway tenant, read back through <see cref="IDatabaseCkModelRepository.TryLookupCkModelAsync" /> —
///     the path the runtime CK cache is rebuilt from — and compared with the compiled DTO as JSON over ALL
///     public properties (<see cref="CkModelJsonComparer" />). There is no hand-written field list: a CK DTO
///     property that the persistence layer does not write and read back fails this test by itself.
///     <para>
///         Corpus: the installed System model, <c>Test-1.0.0</c> and a kitchen-sink v2 model that uses every
///         Phase 0 construct (ckLanguage 2, interfaces with required and optional members, declared and
///         inherited implements, all four access values on type / record / association-role assignments, a
///         method with every field, a static and a minimal one), twice: compiled by MSBuild from YAML
///         (<c>tests/TestCkModelKitchenSink</c>, <c>KitchenSink-1.0.0</c>, through the real compiler and catalog)
///         and built in C# (<see cref="CkV2KitchenSinkModel" />, <c>KitchenSinkCs-1.0.0</c>, which also sets
///         combinations the YAML cannot express in one model).
///     </para>
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class CkMetaModelRoundTripTests(CkModelImportMigrationFixture fixture)
{
    private static readonly CkModelId TestV1ModelId = new("Test-1.0.0");

    [Fact]
    public async Task ClassicModels_SurviveTheMongoRoundTrip()
    {
        await WithThrowawayTenantAsync("rtclassic", async (tenant, tenantId) =>
        {
            var catalogService = fixture.GetService<ICatalogService>();
            var systemId = await GetInstalledSystemIdAsync(tenant);

            var operationResult = new OperationResult();
            await tenant.ImportCkModelAsync(TestV1ModelId, operationResult);
            Assert.False(operationResult.HasErrors);

            foreach (var modelId in new[] { systemId, TestV1ModelId })
            {
                var compiled = await catalogService.GetAsync(modelId, new OperationResult());
                Assert.NotNull(compiled);
                await AssertRoundTripAsync(tenantId, compiled);
            }
        });
    }

    internal static readonly CkModelId YamlKitchenSinkModelId = new("KitchenSink-1.0.0");

    [Fact]
    public async Task CkV2YamlKitchenSink_SurvivesTheMongoRoundTrip()
    {
        await WithThrowawayTenantAsync("rtv2yaml", async (tenant, tenantId) =>
        {
            var operationResult = new OperationResult();
            await tenant.ImportCkModelAsync(YamlKitchenSinkModelId, operationResult);
            Assert.False(operationResult.HasErrors, string.Join("; ", operationResult.Messages));

            var compiled = await fixture.GetService<ICatalogService>()
                .GetAsync(YamlKitchenSinkModelId, new OperationResult());
            Assert.NotNull(compiled);
            // Guard against a vacuous pass: the compiled model really carries every v2 construct.
            Assert.Equal(2, compiled.CkLanguage);
            Assert.Equal(4, compiled.Interfaces!.Count);
            // Phase 1 constructs (F1.3-S2): every one of them is in the compiled model, so the gate covers it.
            var labeled = compiled.Interfaces!.Single(i => i.InterfaceId.Name == "Labeled");
            Assert.NotEmpty(labeled.Extends!);
            Assert.NotEmpty(labeled.Associations!);
            Assert.NotEmpty(labeled.Methods!);
            Assert.True(compiled.Interfaces!.Single(i => i.InterfaceId.Name == "Legacy").Deprecated);
            Assert.NotNull(compiled.Types!.Single(t => t.TypeId.Name == "Thing").Derivable);
            Assert.NotNull(compiled.Types!.Single(t => t.TypeId.Name == "Widget").Visibility);
            Assert.NotNull(compiled.Records!.Single().Derivable);
            Assert.NotNull(compiled.Types!.Single(t => t.TypeId.Name == "Gadget").Associations!.Single().TargetCkInterfaceId);
            Assert.NotNull(compiled.MinEngineVersion);
            Assert.Equal(3, compiled.Types!.Count(t => t.Implements is { Count: > 0 }));
            Assert.Equal(3, compiled.Types!.Single(t => t.TypeId.Name == "Thing").Methods!.Count);
            Assert.Equal(3, compiled.Records!.Single().Attributes!.Count(a => a.Access != null));

            await AssertRoundTripAsync(tenantId, compiled);
        });
    }

    /// <summary>
    ///     Review L20: a type with three interfaces declared in reverse alphabetical order reads back in its declared
    ///     order (MongoDB returns the implements rows in no defined order). The JSON gate compares string arrays in
    ///     order, so any reordering fails here.
    /// </summary>
    [Fact]
    public async Task TypeWithThreeInterfaces_ReadsBackInDeclaredOrder()
    {
        await WithThrowawayTenantAsync("rtl20order", async (tenant, tenantId) =>
        {
            var systemId = await GetInstalledSystemIdAsync(tenant);
            var id = new CkModelId("ImplOrder-1.0.0");
            CkInterfaceDto Interface(string name) => new()
            {
                InterfaceId = new CkInterfaceId($"{name}-1"),
                Attributes =
                [
                    new CkInterfaceAttributeDto
                    {
                        CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("Code-1")),
                        AttributeName = "Code", IsOptional = true
                    }
                ]
            };
            var model = new CkCompiledModelRoot
            {
                ModelId = id,
                CkLanguage = 2,
                Dependencies = [systemId],
                Attributes = [new CkAttributeDto { AttributeId = new CkAttributeId("Code-1"), ValueType = AttributeValueTypesDto.String }],
                Interfaces = [Interface("Alpha"), Interface("Beta"), Interface("Gamma")],
                Types =
                [
                    new CkCompiledTypeDto
                    {
                        TypeId = new CkTypeId("Thing-1"),
                        IsCollectionRoot = true,
                        DerivedFromCkTypeId = new CkId<CkTypeId>(systemId, new CkTypeId("Entity-1")),
                        Implements =
                        [
                            new CkId<CkInterfaceId>(id, new CkInterfaceId("Gamma-1")),
                            new CkId<CkInterfaceId>(id, new CkInterfaceId("Beta-1")),
                            new CkId<CkInterfaceId>(id, new CkInterfaceId("Alpha-1"))
                        ],
                        Attributes =
                        [
                            new CkTypeAttributeDto
                            {
                                CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("Code-1")),
                                AttributeName = "Code", IsOptional = true
                            }
                        ]
                    }
                ]
            };
            await tenant.ImportCkModelAsync(model);

            var readBack = await LookupAsync(tenantId, id);
            Assert.Equal(["Gamma-1", "Beta-1", "Alpha-1"],
                readBack!.Types!.Single().Implements!.Select(i => i.ElementId.FullName));
            await AssertRoundTripAsync(tenantId, model);
        });
    }

    [Fact]
    public async Task CkV2KitchenSink_SurvivesTheMongoRoundTrip()
    {
        await WithThrowawayTenantAsync("rtv2sink", async (tenant, tenantId) =>
        {
            var compiled = CkV2KitchenSinkModel.Build(await GetInstalledSystemIdAsync(tenant));
            await tenant.ImportCkModelAsync(compiled);

            await AssertRoundTripAsync(tenantId, compiled);
        });
    }

    /// <summary>
    ///     Classic models must keep their exact document shape: no new element and no row in the new
    ///     collections, so an engine that predates CK v2 reads them unchanged.
    /// </summary>
    [Fact]
    public async Task ClassicModel_DocumentsCarryNoCkV2Elements()
    {
        await WithThrowawayTenantAsync("rtv1shape", async (tenant, tenantId) =>
        {
            var operationResult = new OperationResult();
            await tenant.ImportCkModelAsync(TestV1ModelId, operationResult);
            Assert.False(operationResult.HasErrors);

            var database = GetTenantDatabase(tenantId);
            var model = await database.GetCollection<BsonDocument>("CkModel")
                .Find(new BsonDocument("_id", TestV1ModelId.FullName))
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("ckLanguage", model.Names);
            Assert.DoesNotContain("minEngineVersion", model.Names);
            Assert.DoesNotContain("dependencyRanges", model.Names);

            // Phase 1 members (F1.3-S2): none of them may appear in any CK document of a v1 import.
            string[] phase1Elements = ["visibility", "derivable", "targetCkInterfaceId", "extends", "deprecated"];
            foreach (var collection in new[]
                     {
                         "CkType", "CkRecord", "CkEnum", "CkAttribute", "CkAssociationRole", "CkTypeAssociation"
                     })
            {
                var documents = await database.GetCollection<BsonDocument>(collection)
                    .Find(new BsonDocument("ckModelId", TestV1ModelId.FullName))
                    .ToListAsync(TestContext.Current.CancellationToken);
                Assert.All(documents, d => Assert.Empty(d.Names.Intersect(phase1Elements)));
            }

            var types = await database.GetCollection<BsonDocument>("CkType")
                .Find(new BsonDocument("ckModelId", TestV1ModelId.FullName))
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.NotEmpty(types);
            Assert.All(types, t =>
            {
                Assert.DoesNotContain("methods", t.Names);
                Assert.All(t["attributes"].AsBsonArray, a => Assert.DoesNotContain("access", a.AsBsonDocument.Names));
            });

            Assert.Equal(0, await database.GetCollection<BsonDocument>("CkInterface")
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(0, await database.GetCollection<BsonDocument>("CkTypeInterfaceImplementation")
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                    cancellationToken: TestContext.Current.CancellationToken));
        });
    }

    /// <summary>
    ///     The v2 documents: the interface rows and the implements rows are owned by the importing model and
    ///     disappear with it when the next version replaces it (DeletePreviousVersion).
    /// </summary>
    [Fact]
    public async Task CkV2KitchenSink_DocumentsAndReplacementByNextVersion()
    {
        await WithThrowawayTenantAsync("rtv2docs", async (tenant, tenantId) =>
        {
            var systemId = await GetInstalledSystemIdAsync(tenant);
            await tenant.ImportCkModelAsync(CkV2KitchenSinkModel.Build(systemId));

            var database = GetTenantDatabase(tenantId);
            var model = await database.GetCollection<BsonDocument>("CkModel")
                .Find(new BsonDocument("_id", CkV2KitchenSinkModel.ModelId.FullName))
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, model["ckLanguage"].AsInt32);

            Assert.Equal(["KitchenSinkCs-1.0.0/Coded-1", "KitchenSinkCs-1.0.0/Labeled-1", "KitchenSinkCs-1.0.0/Legacy-1",
                    "KitchenSinkCs-1.0.0/Named-1"],
                await IdsAsync(database, "CkInterface", "_id"));
            Assert.Equal(
            [
                "KitchenSinkCs-1.0.0/Gadget-1 -> KitchenSinkCs-1.0.0/Coded-1",
                "KitchenSinkCs-1.0.0/Thing-1 -> KitchenSinkCs-1.0.0/Named-1",
                "KitchenSinkCs-1.0.0/Widget-1 -> KitchenSinkCs-1.0.0/Named-1"
            ], await ImplementationsAsync(database));

            // The next version drops one implements entry and one interface: nothing of 1.0.0 is left behind.
            var next = CkV2KitchenSinkModel.Build(systemId, new CkModelId("KitchenSinkCs-1.1.0"));
            next.Interfaces!.RemoveAll(i => i.InterfaceId.Name is "Coded" or "Labeled" or "Legacy");
            next.Types!.Single(t => t.TypeId.Name == "Gadget").Implements = null;
            await tenant.ImportCkModelAsync(next);

            Assert.Equal(["KitchenSinkCs-1.1.0/Named-1"], await IdsAsync(database, "CkInterface", "_id"));
            Assert.Equal(
            [
                "KitchenSinkCs-1.1.0/Thing-1 -> KitchenSinkCs-1.1.0/Named-1",
                "KitchenSinkCs-1.1.0/Widget-1 -> KitchenSinkCs-1.1.0/Named-1"
            ], await ImplementationsAsync(database));
            Assert.All(await database.GetCollection<BsonDocument>("CkTypeInterfaceImplementation")
                    .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(TestContext.Current.CancellationToken),
                d => Assert.Equal(1, d["modelState"].AsInt32));

            await AssertRoundTripAsync(tenantId, next);
        });
    }

    /// <summary>
    ///     Message 91 (CkLanguageNotSupported): a model in a CK language this engine does not know is refused
    ///     before anything is written — persisting it would silently drop the constructs it cannot store.
    /// </summary>
    /// <summary>
    ///     F1.3-S2: a forged compiled model that breaks the visibility/derivable rules of another model is refused on
    ///     IMPORT, not only at compile time (the import's hard resolve runs the F1.2-S3 checks 112/113 against the
    ///     installed model read back from MongoDB — which only works because visibility and derivable survive the
    ///     round trip).
    /// </summary>
    [Theory]
    [InlineData("derive")]
    [InlineData("internal")]
    public async Task ForgedModel_ViolatingDerivableOrVisibility_IsRefusedOnImport(string violation)
    {
        await WithThrowawayTenantAsync("rtforged", async (tenant, tenantId) =>
        {
            var systemId = await GetInstalledSystemIdAsync(tenant);
            var baseId = new CkModelId("ForgeBase-1.0.0");
            await tenant.ImportCkModelAsync(new CkCompiledModelRoot
            {
                ModelId = baseId,
                CkLanguage = 2,
                MinEngineVersion = "3.4.0",
                Dependencies = [systemId],
                Attributes =
                [
                    new CkAttributeDto
                    {
                        AttributeId = new CkAttributeId("Secret-1"), ValueType = AttributeValueTypesDto.String,
                        Visibility = CkVisibilityDto.Internal
                    }
                ],
                Types =
                [
                    new CkCompiledTypeDto
                    {
                        TypeId = new CkTypeId("Base-1"), IsAbstract = true, Derivable = CkDerivableDto.Model,
                        DerivedFromCkTypeId = new CkId<CkTypeId>(systemId, new CkTypeId("Entity-1"))
                    }
                ]
            });

            var forgedId = new CkModelId("Forged-1.0.0");
            var forged = new CkCompiledModelRoot
            {
                ModelId = forgedId,
                CkLanguage = 2,
                MinEngineVersion = "3.4.0",
                Dependencies = [baseId, systemId],
                Types =
                [
                    new CkCompiledTypeDto
                    {
                        TypeId = new CkTypeId("Thing-1"),
                        DerivedFromCkTypeId = violation == "derive"
                            ? new CkId<CkTypeId>(baseId, new CkTypeId("Base-1"))
                            : new CkId<CkTypeId>(systemId, new CkTypeId("Entity-1")),
                        Attributes = violation == "internal"
                            ?
                            [
                                new CkTypeAttributeDto
                                {
                                    CkAttributeId = new CkId<CkAttributeId>(baseId, new CkAttributeId("Secret-1")),
                                    AttributeName = "Secret", IsOptional = true
                                }
                            ]
                            : null
                    }
                ]
            };

            await Assert.ThrowsAnyAsync<Exception>(() => tenant.ImportCkModelAsync(forged));
            Assert.Null(await LookupAsync(tenantId, forgedId));

            // Control: the same model without the violation imports, so the refusal above is the rule, not noise.
            forged.Types![0].DerivedFromCkTypeId = new CkId<CkTypeId>(systemId, new CkTypeId("Entity-1"));
            forged.Types![0].Attributes = null;
            await tenant.ImportCkModelAsync(forged);
            Assert.NotNull(await LookupAsync(tenantId, forgedId));
        });
    }

    [Fact]
    public async Task CkLanguageAboveSupported_IsRejectedBeforeAnythingIsWritten()
    {
        await WithThrowawayTenantAsync("rtv3lang", async (tenant, tenantId) =>
        {
            var model = CkV2KitchenSinkModel.Build(await GetInstalledSystemIdAsync(tenant));
            model.CkLanguage = CkModelPropertiesDto.MaxSupportedCkLanguage + 1;

            var exception = await Assert.ThrowsAnyAsync<Exception>(() => tenant.ImportCkModelAsync(model));
            Assert.Contains("CkLanguageNotSupported", Flatten(exception));

            var database = GetTenantDatabase(tenantId);
            Assert.Equal(0, await database.GetCollection<BsonDocument>("CkModel")
                .CountDocumentsAsync(new BsonDocument("modelId", CkV2KitchenSinkModel.ModelId.Name),
                    cancellationToken: TestContext.Current.CancellationToken));
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

    private async Task AssertRoundTripAsync(string tenantId, CkCompiledModelRoot compiled)
    {
        var readBack = await LookupAsync(tenantId, compiled.ModelId);
        Assert.NotNull(readBack);

        var differences = CkModelJsonComparer.Compare(compiled, readBack);
        Assert.True(differences.Count == 0,
            $"'{compiled.ModelId}' does not survive the MongoDB round trip ({differences.Count} difference(s)). " +
            "Persist the property (entity + class map + write + read-back, see the CLAUDE.md checklist) or, " +
            "if it is legitimately not stored, add it to CkModelJsonComparer.RoundTripIgnoredPaths with a " +
            "justification:" + Environment.NewLine + string.Join(Environment.NewLine, differences));
    }

    internal async Task<CkCompiledModelRoot?> LookupAsync(string tenantId, CkModelId modelId)
    {
        var databaseName = tenantId.ToLowerInvariant();
        var client = fixture.GetService<IAdminRepositoryAccess>().GetRepositoryClient(databaseName);
        var dataSource = new MongoDbRepositoryDataSource(NullLogger<MongoDbRepositoryDataSource>.Instance, client,
            databaseName, tenantId);
        var repository = fixture.GetService<IDatabaseCkModelRepository>();
        return await repository.TryLookupCkModelAsync(modelId, new OperationResult(),
            new TenantDatabaseSourceIdentifier(null, dataSource, tenantId));
    }

    internal async Task WithThrowawayTenantAsync(string prefix, Func<ITenantContext, string, Task> body)
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

    internal async Task<CkModelId> GetInstalledSystemIdAsync(ITenantContext tenant)
    {
        var catalogService = fixture.GetService<ICatalogService>();
        foreach (var version in (await catalogService.ListVersionsAsync("System")).OrderByDescending(v => v.ModelId))
        {
            if (await tenant.IsCkModelExistingAsync(version.ModelId))
            {
                return version.ModelId;
            }
        }

        throw new InvalidOperationException("No installed System model found in the catalogs.");
    }

    private static async Task<List<string>> IdsAsync(IMongoDatabase database, string collection, string field)
    {
        var documents = await database.GetCollection<BsonDocument>(collection)
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(TestContext.Current.CancellationToken);
        return documents.Select(d => d[field].AsString).Order(StringComparer.Ordinal).ToList();
    }

    private static async Task<List<string>> ImplementationsAsync(IMongoDatabase database)
    {
        var documents = await database.GetCollection<BsonDocument>("CkTypeInterfaceImplementation")
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(TestContext.Current.CancellationToken);
        return documents.Select(d => $"{d["ckTypeId"].AsString} -> {d["ckInterfaceId"].AsString}")
            .Order(StringComparer.Ordinal).ToList();
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
