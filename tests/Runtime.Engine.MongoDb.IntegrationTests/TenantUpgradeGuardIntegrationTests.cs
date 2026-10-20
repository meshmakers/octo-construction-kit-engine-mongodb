using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.UpgradeGuard;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Tenant upgrade guard (CK v2 F2.5, AB#6867) against a real tenant database: the guard reads the persisted
///     models (exact pins, dependency ranges, <c>usedSurface</c>, types) through <c>ITenantContext.EvaluateCkUpdateAsync</c>,
///     reaches the same verdicts as with in-memory models, and writes nothing. This extends the F1.3 round-trip gate:
///     a range-retaining dependent is rebuilt from MongoDB and judged by the engine's surface checker.
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class TenantUpgradeGuardIntegrationTests(CkModelImportMigrationFixture fixture)
{
    [Fact]
    public async Task PersistedModels_AreJudgedLikeInMemoryModels_AndNothingIsWritten()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"upgguard{Guid.NewGuid():N}"[..20];
        try
        {
            await CreateChildAsync(tenantId);
            var tenant = await GetChildAsync(tenantId);
            var system = await GetInstalledSystemIdAsync(tenant);
            var database = GetTenantDatabase(tenantId);

            await tenant.ImportCkModelAsync(BuildBase("GBase-1.0.0", system, withExtra: false));
            var rangeDependent = BuildDependent("GRange-1.0.0", "GBase-1.0.0", system, rangeRetaining: true);
            rangeDependent.DependencyRanges![0].UsedSurface = ["GBase@1/Thing-1"];
            await tenant.ImportCkModelAsync(rangeDependent);
            await tenant.ImportCkModelAsync(BuildDependent("GExact-1.0.0", "GBase-1.0.0", system, rangeRetaining: false));
            var before = await SnapshotAsync(database);

            // 1. Additive minor: the exact-pinned model breaks (it is the sbeg case), the range-retaining one does not.
            var additive = await tenant.EvaluateCkUpdateAsync([BuildBase("GBase-1.1.0", system, withExtra: true)],
                [new CkModelId("GExact-1.2.0")], TestContext.Current.CancellationToken);

            var exact = Assert.Single(additive.Blockers);
            Assert.Equal(CkUpgradeBlockerKind.ExactPin, exact.Kind);
            Assert.Equal("GExact-1.0.0", exact.Dependent.FullName);
            Assert.Equal("GBase-1.0.0", exact.Subject);
            Assert.Equal("GBase-1.1.0", exact.Update.FullName);
            Assert.Equal("GExact-1.2.0", exact.SuggestedDependentUpdate?.FullName);
            Assert.Empty(additive.Notes);

            // 2. The candidate no longer defines the type the persisted range-retaining dependent derives from.
            var removed = BuildBase("GBase-1.1.0", system, withExtra: true);
            removed.Types!.Clear();
            var breaking = await tenant.EvaluateCkUpdateAsync([removed], null, TestContext.Current.CancellationToken);

            Assert.Contains(breaking.Blockers, b => b.Dependent.Name == "GExact" && b.Kind == CkUpgradeBlockerKind.ExactPin);
            var element = Assert.Single(breaking.Blockers, b => b.Dependent.Name == "GRange");
            Assert.Equal(CkUpgradeBlockerKind.ElementRemoved, element.Kind);
            Assert.Contains("Thing-1", element.Subject);

            // 3. Below the floor of the persisted range (the dependent was imported against 1.0.0, floor 1.0.0).
            var floor = await tenant.EvaluateCkUpdateAsync([BuildBase("GBase-0.9.0", system, withExtra: false)], null,
                TestContext.Current.CancellationToken);
            // Another major than the installed one: a parallel install, never a blocker.
            Assert.DoesNotContain(floor.Blockers, b => b.Dependent.Name == "GRange" && b.Kind != CkUpgradeBlockerKind.ExactPin);
            var parallel = await tenant.EvaluateCkUpdateAsync([BuildBase("GBase-2.0.0", system, withExtra: false)], null,
                TestContext.Current.CancellationToken);
            Assert.Empty(parallel.Blockers);

            // Read-only: the tenant's model documents are byte-identical, every state is unchanged.
            Assert.Equal(before, await SnapshotAsync(database));
            Assert.Equal("GRange-1.0.0=1, GExact-1.0.0=1, GBase-1.0.0=1", await StatesAsync(database, "GRange", "GExact", "GBase"));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    [Fact]
    public async Task ExplicitCaller_JudgesAServiceManagedSystemCandidate_TheDefaultDoesNot()
    {
        // AB#6966: a file import of System must be judged; every service-driven path keeps skipping it.
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"upgsys{Guid.NewGuid():N}"[..20];
        try
        {
            await CreateChildAsync(tenantId);
            var tenant = await GetChildAsync(tenantId);
            var system = await GetInstalledSystemIdAsync(tenant);
            await tenant.ImportCkModelAsync(BuildBase("SBase-1.0.0", system, withExtra: false));
            await tenant.ImportCkModelAsync(BuildDependent("SExact-1.0.0", "SBase-1.0.0", system, rangeRetaining: false));
            var database = GetTenantDatabase(tenantId);
            var before = await SnapshotAsync(database);

            var newerSystem = new CkCompiledModelRoot
            {
                ModelId = new CkModelId("System", new Version(system.Version.Major, system.Version.Minor + 1, 0).ToString()),
                Types = []
            };

            var byDefault = await tenant.EvaluateCkUpdateAsync([newerSystem], null, TestContext.Current.CancellationToken);
            Assert.Empty(byDefault.Blockers);

            var explicitCaller =
                await tenant.EvaluateExplicitCkImportAsync([newerSystem], null, TestContext.Current.CancellationToken);
            Assert.True(explicitCaller.IsBlocked);
            Assert.Contains(explicitCaller.Blockers,
                b => b.Dependent.FullName == "SExact-1.0.0" && b.Kind == CkUpgradeBlockerKind.ExactPin &&
                     b.Update.Name == "System");

            Assert.Equal(before, await SnapshotAsync(database));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    [Fact]
    public async Task PersistedLegacyRangeRetainingModelWithoutUsedSurface_IsCheckedByReferences_AndAnnounced()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"upgleg{Guid.NewGuid():N}"[..20];
        try
        {
            await CreateChildAsync(tenantId);
            var tenant = await GetChildAsync(tenantId);
            var system = await GetInstalledSystemIdAsync(tenant);

            await tenant.ImportCkModelAsync(BuildBase("LBase-1.0.0", system, withExtra: false));
            // Compiled before AB#4472: no usedSurface on the range.
            await tenant.ImportCkModelAsync(BuildDependent("LRange-1.0.0", "LBase-1.0.0", system, rangeRetaining: true));

            var result = await tenant.EvaluateCkUpdateAsync([BuildBase("LBase-1.1.0", system, withExtra: true)], null,
                TestContext.Current.CancellationToken);

            Assert.Empty(result.Blockers);
            Assert.Contains(result.Notes, n => n.Contains("LRange-1.0.0") && n.Contains("no usedSurface"));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    [Fact]
    public async Task InstalledUniqueIndex_IsReadBackAsBaseline_SoOnlyARealNewIndexBlocks()
    {
        // AB#7125: the installed baseline used to carry no indexes (TryLookupCkModelAsync never mapped them back), so every
        // index of the update looked ADDED and an added unique index was rated Major ("existing data may violate").
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"upgidx{Guid.NewGuid():N}"[..20];
        try
        {
            await CreateChildAsync(tenantId);
            var tenant = await GetChildAsync(tenantId);
            var system = await GetInstalledSystemIdAsync(tenant);

            // Installed: IUnique has a unique-not-deleted index on Code since its first version, IPlain has none.
            await tenant.ImportCkModelAsync(BuildBase("IUnique-1.0.0", system, withExtra: false,
                uniqueIndexOnCode: IndexTypeDto.UniqueNotDeleted));
            var uniqueDependent = BuildDependent("IUniqueDep-1.0.0", "IUnique-1.0.0", system, rangeRetaining: true);
            uniqueDependent.DependencyRanges![0].UsedSurface = ["IUnique@1/Thing-1"];
            await tenant.ImportCkModelAsync(uniqueDependent);
            await tenant.ImportCkModelAsync(BuildBase("IPlain-1.0.0", system, withExtra: false));
            var plainDependent = BuildDependent("IPlainDep-1.0.0", "IPlain-1.0.0", system, rangeRetaining: true);
            plainDependent.DependencyRanges![0].UsedSurface = ["IPlain@1/Thing-1"];
            await tenant.ImportCkModelAsync(plainDependent);

            // The same index in the higher version is not a change: no blocker (was: ElementChanged, Major).
            var same = await tenant.EvaluateCkUpdateAsync(
                [BuildBase("IUnique-1.1.0", system, withExtra: true, uniqueIndexOnCode: IndexTypeDto.UniqueNotDeleted)], null,
                TestContext.Current.CancellationToken);
            Assert.Empty(same.Blockers);

            // A version that really ADDS a unique index to the used type still blocks as before.
            var added = await tenant.EvaluateCkUpdateAsync(
                [BuildBase("IPlain-1.1.0", system, withExtra: false, uniqueIndexOnCode: IndexTypeDto.UniqueNotDeleted)], null,
                TestContext.Current.CancellationToken);
            var blocker = Assert.Single(added.Blockers);
            Assert.Equal(CkUpgradeBlockerKind.ElementChanged, blocker.Kind);
            Assert.Equal("IPlainDep-1.0.0", blocker.Dependent.FullName);
            Assert.Contains("index", blocker.Message, StringComparison.OrdinalIgnoreCase);

            // Dropping the installed index is not breaking for dependents (it is judged against the baseline, not as "added").
            var removed = await tenant.EvaluateCkUpdateAsync([BuildBase("IUnique-1.2.0", system, withExtra: false)], null,
                TestContext.Current.CancellationToken);
            Assert.Empty(removed.Blockers);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    private static async Task<string> SnapshotAsync(IMongoDatabase database)
    {
        var documents = await database.GetCollection<BsonDocument>("CkModel").Find(FilterDefinition<BsonDocument>.Empty)
            .SortBy(d => d["_id"]).ToListAsync(TestContext.Current.CancellationToken);
        return string.Join("\n", documents.Select(d => d.ToJson()));
    }

    private static async Task<string> StatesAsync(IMongoDatabase database, params string[] modelNames)
    {
        var documents = await database.GetCollection<BsonDocument>("CkModel")
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(TestContext.Current.CancellationToken);
        return string.Join(", ", modelNames.SelectMany(name => documents
            .Where(d => d["modelId"].AsString == name)
            .Select(d => $"{d["_id"]}={d["modelState"]}")));
    }

    /// <summary>A base model: type Thing (derived from System@N/Entity) with an own attribute Code.</summary>
    private static CkCompiledModelRoot BuildBase(string modelId, CkModelId system, bool withExtra,
        IndexTypeDto? uniqueIndexOnCode = null)
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
            Description = "AB#6867 upgrade guard base model",
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
                    Indexes = uniqueIndexOnCode is { } indexType
                        ?
                        [
                            new CkTypeIndexDto
                            {
                                IndexType = indexType,
                                Fields = [new CkIndexFieldsDto { AttributePaths = ["Code"] }]
                            }
                        ]
                        : null,
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

    /// <summary>A dependent: type Gadget derived from the base's Thing, range-retaining or exact-pinned.</summary>
    private static CkCompiledModelRoot BuildDependent(string modelId, string baseModelId, CkModelId system,
        bool rangeRetaining)
    {
        var baseId = new CkModelId(baseModelId);
        var baseRef = rangeRetaining ? baseId.ToMajorQualified() : baseId;
        return new CkCompiledModelRoot
        {
            ModelId = new CkModelId(modelId),
            Description = "AB#6867 upgrade guard dependent",
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
                    DerivedFromCkTypeId = new CkId<CkTypeId>(baseRef, new CkTypeId("Thing-1"))
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
