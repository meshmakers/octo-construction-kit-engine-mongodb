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
///         method with every field and a minimal one). Until the engine compiler hand-off H1b the kitchen sink
///         is built in C# (<see cref="CkV2KitchenSinkModel" />); P5 adds the MSBuild-compiled YAML twin.
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

            Assert.Equal(["KitchenSink-1.0.0/Coded-1", "KitchenSink-1.0.0/Named-1"],
                await IdsAsync(database, "CkInterface", "_id"));
            Assert.Equal(
            [
                "KitchenSink-1.0.0/Gadget-1 -> KitchenSink-1.0.0/Coded-1",
                "KitchenSink-1.0.0/Thing-1 -> KitchenSink-1.0.0/Named-1",
                "KitchenSink-1.0.0/Widget-1 -> KitchenSink-1.0.0/Named-1"
            ], await ImplementationsAsync(database));

            // The next version drops one implements entry and one interface: nothing of 1.0.0 is left behind.
            var next = CkV2KitchenSinkModel.Build(systemId, new CkModelId("KitchenSink-1.1.0"));
            next.Interfaces!.RemoveAll(i => i.InterfaceId.Name == "Coded");
            next.Types!.Single(t => t.TypeId.Name == "Gadget").Implements = null;
            await tenant.ImportCkModelAsync(next);

            Assert.Equal(["KitchenSink-1.1.0/Named-1"], await IdsAsync(database, "CkInterface", "_id"));
            Assert.Equal(
            [
                "KitchenSink-1.1.0/Thing-1 -> KitchenSink-1.1.0/Named-1",
                "KitchenSink-1.1.0/Widget-1 -> KitchenSink-1.1.0/Named-1"
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
