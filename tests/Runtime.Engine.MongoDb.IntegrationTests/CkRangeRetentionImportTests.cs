using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.ConstructionKit.Engine.Resolvers.RangeRetention;
using Meshmakers.Octo.ConstructionKit.Engine.Resolvers.Repository;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#5665 / AB#5914 (CK v2 range retention): a range-retaining model (dependency range + floor, major-qualified
///     references <c>RrBase@1/...</c>) survives the MongoDB round trip and stays <c>Available</c> when its
///     dependency gets an additive minor — while a classic exact-pinned model goes <c>ResolveFailed</c>.
///     <para>
///         The upgraded dependency is a test base model, not System: a tenant context re-imports its
///         service's embedded System version whenever that exact version is missing
///         (<c>TenantContext.UpdateSystemCkModelAsync</c>), so a System bump only sticks when the service
///         embeds it (that is what the AB#5666 live demo does). Runs in throwaway child tenants.
///     </para>
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class CkRangeRetentionImportTests(CkModelImportMigrationFixture fixture)
{
    private static readonly RtCkId<CkTypeId> GadgetTypeId = new("RangeDep/Gadget");

    [Fact]
    public async Task RangeRetainingModel_StaysAvailableAcrossAdditiveMinor_ExactPinnedModelFails()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"rangeret{Guid.NewGuid():N}"[..20];
        try
        {
            await CreateChildAsync(tenantId);
            var tenant = await GetChildAsync(tenantId);
            var system = await GetInstalledSystemIdAsync(tenant);
            var database = GetTenantDatabase(tenantId);

            await tenant.ImportCkModelAsync(BuildBase("RrBase-1.0.0", system, withExtra: false));
            var rangeDependent = BuildDependent("RangeDep-1.0.0", "RrBase-1.0.0", system, rangeRetaining: true);
            var exactDependent = BuildDependent("ExactDep-1.0.0", "RrBase-1.0.0", system, rangeRetaining: false);
            await tenant.ImportCkModelAsync(rangeDependent);
            await tenant.ImportCkModelAsync(exactDependent);
            Assert.Equal("RangeDep-1.0.0=1, ExactDep-1.0.0=1", await StatesAsync(database, "RangeDep", "ExactDep"));

            // Persistence (AB#4589 lesson): range + floor on the CkModel document, version-less references on
            // the inheritance rows; the caller's instance was not bound in place by the import.
            var modelDocument = await SingleAsync(database, "CkModel", "_id", "RangeDep-1.0.0");
            var ranges = modelDocument["dependencyRanges"].AsBsonArray.Select(r => r.AsBsonDocument).ToList();
            Assert.Contains(ranges, r => r["range"] == "RrBase-[1.0,2.0)" && r["floor"] == "1.0.0");
            Assert.False((await SingleAsync(database, "CkModel", "_id", "ExactDep-1.0.0")).Contains("dependencyRanges"));
            var inheritance = await SingleAsync(database, "CkTypeInheritance", "ckModelId", "RangeDep-1.0.0");
            Assert.Equal("RrBase@1/Thing-1", inheritance["baseCkTypeId"].AsString);
            Assert.Equal("RrBase@1/Thing-1", rangeDependent.Types!.Single().DerivedFromCkTypeId!.FullName);

            // Additive minor of the dependency: one optional attribute more. No dependent is touched.
            await tenant.ImportCkModelAsync(BuildBase("RrBase-1.1.0", system, withExtra: true));

            Assert.Equal("RangeDep-1.0.0=1, ExactDep-1.0.0=2, RrBase-1.1.0=1",
                await StatesAsync(database, "RangeDep", "ExactDep", "RrBase"));

            // The runtime cache is rebuilt from MongoDB and binds RrBase@1 to the installed version.
            var cacheService = fixture.GetService<ICkCacheService>();
            if (cacheService.IsTenantLoaded(tenantId))
            {
                cacheService.Unload(tenantId);
            }

            await tenant.LoadCacheForTenantAsync();
            var gadget = cacheService.GetRtCkType(tenantId, GadgetTypeId);
            Assert.Equal("RrBase-1.1.0/Thing-1", gadget.DerivedFromCkTypeId!.FullName);
            Assert.Contains(gadget.AllAttributes.Values, a => a.AttributeName == "Code");
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    [Fact]
    public async Task InstalledVersionBelowFloor_GoesResolveFailed_AndNamesRangeFloorAndInstalled()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"rangeflr{Guid.NewGuid():N}"[..20];
        try
        {
            await CreateChildAsync(tenantId);
            var tenant = await GetChildAsync(tenantId);
            var system = await GetInstalledSystemIdAsync(tenant);
            var database = GetTenantDatabase(tenantId);

            await tenant.ImportCkModelAsync(BuildBase("RrBase-1.1.0", system, withExtra: true));
            await tenant.ImportCkModelAsync(BuildDependent("FloorDep-1.0.0", "RrBase-1.1.0", system,
                rangeRetaining: true));
            Assert.Equal("FloorDep-1.0.0=1", await StatesAsync(database, "FloorDep"));

            // A downgrade below the floor (1.1.0) must not be accepted silently.
            await tenant.ImportCkModelAsync(BuildBase("RrBase-1.0.0", system, withExtra: false));
            Assert.Equal("FloorDep-1.0.0=2", await StatesAsync(database, "FloorDep"));

            var models = await database.GetCollection<CkModel>("CkModel").Find(FilterDefinition<CkModel>.Empty)
                .ToListAsync(TestContext.Current.CancellationToken);
            var description = DatabaseCkModelRepository.DescribeUnmetDependencies(
                models.Single(m => m.Id.Name == "FloorDep"), models);
            Assert.Equal("RrBase-[1.0,2.0) (floor 1.1.0): installed RrBase-1.0.0", description);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    /// <summary>
    ///     AB#4472 / AB#6273 (usedSurface E2E): the usedSurface list and hash a range-retaining model carries per
    ///     dependency range reach the tenant's <c>CkModel</c> document through the real tenant import
    ///     (<c>ITenantContext.ImportCkModelAsync</c>, the code path bot-services runs for ImportCk) and read back
    ///     verbatim; an exact-pinned (v1) model document carries no such field.
    /// </summary>
    [Fact]
    public async Task UsedSurface_PersistsThroughTenantImport_ExactPinnedModelHasNone()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"rangeuse{Guid.NewGuid():N}"[..20];
        try
        {
            await CreateChildAsync(tenantId);
            var tenant = await GetChildAsync(tenantId);
            var system = await GetInstalledSystemIdAsync(tenant);
            var database = GetTenantDatabase(tenantId);

            await tenant.ImportCkModelAsync(BuildBase("RrBase-1.0.0", system, withExtra: false));
            var rangeDependent = BuildDependent("UseDep-1.0.0", "RrBase-1.0.0", system, rangeRetaining: true);
            CkUsedSurfaceCollector.Apply(rangeDependent, null);
            var exactDependent = BuildDependent("UseExact-1.0.0", "RrBase-1.0.0", system, rangeRetaining: false);
            await tenant.ImportCkModelAsync(rangeDependent);
            await tenant.ImportCkModelAsync(exactDependent);
            Assert.Equal("UseDep-1.0.0=1, UseExact-1.0.0=1", await StatesAsync(database, "UseDep", "UseExact"));

            var expected = rangeDependent.DependencyRanges!.ToDictionary(r => r.Range.Name);
            Assert.Equal(["RrBase@1/Thing-1"], expected["RrBase"].UsedSurface);
            Assert.Equal([$"System@{system.Version.Major}/Name-1"], expected["System"].UsedSurface);

            var document = await SingleAsync(database, "CkModel", "_id", "UseDep-1.0.0");
            foreach (var range in document["dependencyRanges"].AsBsonArray.Select(r => r.AsBsonDocument))
            {
                var name = range["range"].AsString.Split('-')[0];
                Assert.Equal(expected[name].UsedSurface, range["usedSurface"].AsBsonArray.Select(v => v.AsString));
                Assert.Equal(expected[name].UsedSurfaceHash, range["usedSurfaceHash"].AsString);
                Assert.Equal(CkUsedSurfaceCollector.Hash(expected[name].UsedSurface!),
                    range["usedSurfaceHash"].AsString);
            }

            var exactDocument = await SingleAsync(database, "CkModel", "_id", "UseExact-1.0.0");
            Assert.False(exactDocument.Contains("dependencyRanges"));
            Assert.DoesNotContain("usedSurface", exactDocument.ToJson(), StringComparison.Ordinal);

            // Read back through the typed repository entity (the path the runtime and the catalog export use).
            var typed = await database.GetCollection<CkModel>("CkModel")
                .Find(Builders<CkModel>.Filter.Eq("_id", "UseDep-1.0.0"))
                .SingleAsync(TestContext.Current.CancellationToken);
            var dto = typed.DependencyRanges!.Single(d => d.Range.StartsWith("RrBase-", StringComparison.Ordinal));
            Assert.Equal(["RrBase@1/Thing-1"], dto.UsedSurface);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    /// <summary>
    ///     D3 / review M6: index maintenance works on the persisted rows, where a range-retaining model stores
    ///     its base types major-qualified. The collection root RrBase/Thing (base System@N/Entity) must get the
    ///     System Entity index, and the index declared on the derived RangeDep/Gadget (base RrBase@1/Thing,
    ///     attribute System@N/Name) must land in Thing's collection.
    /// </summary>
    [Fact]
    public async Task RangeRetainingTypes_IndexMaintenanceFollowsMajorQualifiedBaseTypes()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"rangeidx{Guid.NewGuid():N}"[..20];
        try
        {
            await CreateChildAsync(tenantId);
            var tenant = await GetChildAsync(tenantId);
            var system = await GetInstalledSystemIdAsync(tenant);
            var database = GetTenantDatabase(tenantId);

            await tenant.ImportCkModelAsync(BuildBase("RrBase-1.0.0", system, withExtra: false));
            await tenant.ImportCkModelAsync(BuildDependent("RangeDep-1.0.0", "RrBase-1.0.0", system,
                rangeRetaining: true));

            var collectionName = (await (await database.ListCollectionNamesAsync(
                        cancellationToken: TestContext.Current.CancellationToken))
                    .ToListAsync(TestContext.Current.CancellationToken))
                .Single(n => n.EndsWith("RrBaseThing", StringComparison.Ordinal));
            var indexes = await (await database.GetCollection<BsonDocument>(collectionName).Indexes
                    .ListAsync(TestContext.Current.CancellationToken))
                .ToListAsync(TestContext.Current.CancellationToken);
            var names = indexes.Select(i => i["name"].AsString).ToList();
            var keys = indexes.Select(i => i["key"].AsBsonDocument.Names.ToList()).ToList();

            Assert.Contains(names, n => n.StartsWith("SystemEntity", StringComparison.Ordinal));
            Assert.Contains(keys, k => k.Any(f => f.Contains("label", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    /// <summary>
    ///     Range retention (AB#5665 / AB#5914): an additive System minor imported into a tenant that holds
    ///     exact-pinned AND range-retaining models. The exact pins on the old System go ResolveFailed, the
    ///     range-retaining models stay Available, and the import itself must not throw ("Sequence contains more
    ///     than one matching element" with a resolver that matches ranges by overlap).
    /// </summary>
    [Fact]
    public async Task AdditiveSystemMinor_MixedExactAndRangeModels_RangeModelsStayAvailable()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"rangesys{Guid.NewGuid():N}"[..20];
        try
        {
            await CreateChildAsync(tenantId);
            var tenant = await GetChildAsync(tenantId);
            var system = await GetInstalledSystemIdAsync(tenant);
            var database = GetTenantDatabase(tenantId);

            // Exact pins first (like the service models System.Bot / System.Communication), then range models.
            await tenant.ImportCkModelAsync(ExactOnSystem("ExactBot-3.4.0", system));
            await tenant.ImportCkModelAsync(ExactOnSystem("ExactComm-3.40.0", system,
                new CkModelId("ExactBot-3.4.0")));
            await tenant.ImportCkModelAsync(BuildBase("RrBase-1.0.0", system, withExtra: false));
            await tenant.ImportCkModelAsync(BuildDependent("RangeDep-1.0.0", "RrBase-1.0.0", system,
                rangeRetaining: true));
            Assert.Equal("ExactBot-3.4.0=1, ExactComm-3.40.0=1, RrBase-1.0.0=1, RangeDep-1.0.0=1",
                await StatesAsync(database, "ExactBot", "ExactComm", "RrBase", "RangeDep"));

            // Import through the CK model repository (ExecuteImport → ValidateDependencies, where D2 threw).
            // The tenant-level ImportCkModelAsync would afterwards re-import the host's EMBEDDED System 2.5.0
            // (TenantContext.UpdateSystemCkModelAsync has no downgrade guard) — in production every service
            // embeds the new System, in this test host only the old one exists.
            var nextSystem = await NextSystemMinorAsync(system);
            var dataSource = new MongoDbRepositoryDataSource(NullLogger<MongoDbRepositoryDataSource>.Instance,
                fixture.GetService<IAdminRepositoryAccess>().GetRepositoryClient(tenantId.ToLowerInvariant()),
                tenantId.ToLowerInvariant(), tenantId);
            await fixture.GetService<IDatabaseCkModelRepository>().UpdateModelAsync(nextSystem,
                new TenantDatabaseSourceIdentifier(null, dataSource, tenantId));

            Assert.Equal(
                $"{nextSystem.ModelId}=1, ExactBot-3.4.0=2, ExactComm-3.40.0=2, RrBase-1.0.0=1, RangeDep-1.0.0=1",
                await StatesAsync(database, "System", "ExactBot", "ExactComm", "RrBase", "RangeDep"));

            // The runtime graph of the range-retaining models binds to the new System (cache built from Mongo).
            var graph = await fixture.GetService<IRepositoryModelResolver>().HardResolveAsync(
                [new CkModelId("RangeDep-1.0.0")], new OriginFileResolver("-"), new OperationResult(),
                new TenantDatabaseSourceIdentifier(null, dataSource, tenantId));
            Assert.Equal($"{nextSystem.ModelId.FullName}/Entity-1",
                graph.Types[new CkId<CkTypeId>("RrBase-1.0.0/Thing-1")].DerivedFromCkTypeId!.FullName);
            Assert.Equal("RrBase-1.0.0/Thing-1",
                graph.Types[new CkId<CkTypeId>("RangeDep-1.0.0/Gadget-1")].DerivedFromCkTypeId!.FullName);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    private static CkCompiledModelRoot ExactOnSystem(string modelId, CkModelId system, params CkModelId[] more)
    {
        var id = new CkModelId(modelId);
        return new CkCompiledModelRoot
        {
            ModelId = id,
            Description = "AB#5665 exact-pinned model on System",
            Dependencies = [system, .. more],
            Types =
            [
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId($"{id.Name}Thing-1"),
                    DerivedFromCkTypeId = new CkId<CkTypeId>(system, new CkTypeId("Entity-1"))
                }
            ]
        };
    }

    /// <summary>The installed System re-versioned as the next minor with one additive optional attribute.</summary>
    private async Task<CkCompiledModelRoot> NextSystemMinorAsync(CkModelId system)
    {
        var copy = (await fixture.GetService<ICatalogService>().GetAsync(system, new OperationResult()))!;
        var next = new CkModelId(system.Name, new CkVersion(system.Version.Major, system.Version.Minor + 1, 0));
        copy.ModelId = next;
        RewriteOwnReferences(copy, system, next);
        copy.Attributes!.Add(new CkAttributeDto
        {
            AttributeId = new CkAttributeId("RangeRetentionDemo-1"), ValueType = AttributeValueTypesDto.String
        });
        return copy;
    }

    private static void RewriteOwnReferences(CkCompiledModelRoot model, CkModelId from, CkModelId to)
    {
        CkId<T>? Map<T>(CkId<T>? id) where T : IComparable<T>, ICkElementId =>
            id != null && id.ModelId == from ? new CkId<T>(to, id.ElementId) : id;

        foreach (var attribute in model.Attributes ?? [])
        {
            attribute.ValueCkEnumId = Map(attribute.ValueCkEnumId);
            attribute.ValueCkRecordId = Map(attribute.ValueCkRecordId);
        }

        foreach (var role in model.AssociationRoles ?? [])
        {
            role.Attributes?.ForEach(a => a.CkAttributeId = Map(a.CkAttributeId)!);
        }

        foreach (var record in model.Records ?? [])
        {
            record.DerivedFromCkRecordId = Map(record.DerivedFromCkRecordId);
            record.Attributes?.ForEach(a => a.CkAttributeId = Map(a.CkAttributeId)!);
        }

        foreach (var type in model.Types ?? [])
        {
            type.DerivedFromCkTypeId = Map(type.DerivedFromCkTypeId);
            type.Attributes?.ForEach(a => a.CkAttributeId = Map(a.CkAttributeId)!);
            foreach (var association in type.Associations ?? [])
            {
                association.CkRoleId = Map(association.CkRoleId)!;
                association.TargetCkTypeId = Map(association.TargetCkTypeId)!;
                association.TargetCkAttributeIds = association.TargetCkAttributeIds?.Select(a => Map(a)!).ToList();
            }
        }
    }

    /// <summary>A base model: type Thing (derived from System@N/Entity) with an own attribute Code.</summary>
    private static CkCompiledModelRoot BuildBase(string modelId, CkModelId system, bool withExtra)
    {
        var id = new CkModelId(modelId);
        var systemMajor = system.ToMajorQualified();
        var attributes = new List<CkAttributeDto>
        {
            new() { AttributeId = new CkAttributeId("Code-1"), ValueType = AttributeValueTypesDto.String }
        };
        if (withExtra)
        {
            attributes.Add(new CkAttributeDto
            {
                AttributeId = new CkAttributeId("Extra-1"), ValueType = AttributeValueTypesDto.String
            });
        }

        return new CkCompiledModelRoot
        {
            ModelId = id,
            Description = "AB#5665 range retention base model",
            Dependencies = [system],
            DependencyRanges = [SystemRange(system)],
            Attributes = attributes,
            Types =
            [
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Thing-1"),
                    IsCollectionRoot = true,
                    DerivedFromCkTypeId = new CkId<CkTypeId>(systemMajor, new CkTypeId("Entity-1")),
                    Attributes =
                    [
                        new CkTypeAttributeDto
                        {
                            CkAttributeId = new CkId<CkAttributeId>(id, new CkAttributeId("Code-1")),
                            AttributeName = "Code",
                            IsOptional = true
                        }
                    ]
                }
            ]
        };
    }

    /// <summary>A dependent: type Gadget derived from RrBase/Thing, range-retaining or exact-pinned.</summary>
    private static CkCompiledModelRoot BuildDependent(string modelId, string baseModelId, CkModelId system,
        bool rangeRetaining)
    {
        var baseId = new CkModelId(baseModelId);
        var baseRef = rangeRetaining ? baseId.ToMajorQualified() : baseId;
        return new CkCompiledModelRoot
        {
            ModelId = new CkModelId(modelId),
            Description = "AB#5665 range retention dependent",
            Dependencies = [baseId, system],
            DependencyRanges = rangeRetaining
                ?
                [
                    new CkModelDependencyDto
                    {
                        Range = new CkModelIdVersionRange(baseId.Name, "[1.0,2.0)"),
                        Floor = baseId.Version.ToString()
                    },
                    SystemRange(system)
                ]
                : null,
            Types =
            [
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId("Gadget-1"),
                    DerivedFromCkTypeId = new CkId<CkTypeId>(baseRef, new CkTypeId("Thing-1")),
                    // D3: an attribute of a dependency (major-qualified when range-retaining) carrying an index.
                    Attributes =
                    [
                        new CkTypeAttributeDto
                        {
                            CkAttributeId = new CkId<CkAttributeId>(
                                rangeRetaining ? system.ToMajorQualified() : system, new CkAttributeId("Name-1")),
                            AttributeName = "Label",
                            IsOptional = true
                        }
                    ],
                    Indexes =
                    [
                        new CkTypeIndexDto
                        {
                            IndexType = IndexTypeDto.Ascending,
                            Fields = [new CkIndexFieldsDto { AttributePaths = ["Label"] }]
                        }
                    ]
                }
            ]
        };
    }

    private static CkModelDependencyDto SystemRange(CkModelId system) => new()
    {
        Range = new CkModelIdVersionRange(system.Name,
            $"[{system.Version.Major}.0,{system.Version.Major + 1}.0)"),
        Floor = system.Version.ToString()
    };

    private async Task<CkModelId> GetInstalledSystemIdAsync(ITenantContext tenant)
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

    private static async Task<string> StatesAsync(IMongoDatabase database, params string[] modelNames)
    {
        var documents = await database.GetCollection<BsonDocument>("CkModel")
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(TestContext.Current.CancellationToken);
        return string.Join(", ", modelNames.SelectMany(name => documents
            .Where(d => d["modelId"].AsString == name)
            .Select(d => $"{d["_id"]}={d["modelState"]}")));
    }

    private static Task<BsonDocument> SingleAsync(IMongoDatabase database, string collection, string field,
        string value)
    {
        return database.GetCollection<BsonDocument>(collection).Find(new BsonDocument(field, value))
            .SingleAsync(TestContext.Current.CancellationToken);
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

    private async Task CreateChildAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task<ITenantContext> GetChildAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        var child = await systemContext.GetChildTenantContextAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return child;
    }
}
