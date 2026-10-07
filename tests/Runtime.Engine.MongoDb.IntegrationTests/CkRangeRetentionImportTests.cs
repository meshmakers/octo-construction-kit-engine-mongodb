using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#5665 (CK v2 Phase 0 spike): a range-retaining model (dependency range + floor, major-qualified
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
