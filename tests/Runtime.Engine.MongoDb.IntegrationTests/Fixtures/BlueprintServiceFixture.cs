using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Engine.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Blueprints;

using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
/// Fixture for full IBlueprintService integration tests. Builds on
/// ImportTestCkModelFixture (which loads the Test CK model into the system
/// tenant) and adds:
/// <list type="bullet">
/// <item><description>MongoDB blueprint support (history + backup + provider)</description></item>
/// <item><description>A LocalFileSystemBlueprintCatalog pointed at
/// <c>./TestBlueprints/</c> in the test output directory</description></item>
/// </list>
/// </summary>
public class BlueprintServiceFixture : ImportTestCkModelFixture
{
    public const string TestBlueprintsRelativePath = "TestBlueprints";

    public BlueprintServiceFixture()
    {
        // NOTE: Calling AddRuntimeEngine() here would re-register the in-memory
        // RuntimeRepositoryProvider on top of the MongoRuntimeRepositoryProvider
        // that ServiceCollectionFixture installed via AddMongoDbRuntimeRepository().
        // We register the Mongo blueprint history directly to avoid that race.
        Services.AddTransient<ITenantBlueprintHistory, MongoTenantBlueprintHistory>();

        Services.Configure<LocalFileSystemBlueprintCatalogOptions>(opts =>
        {
            opts.RootPath = Path.Combine(AppContext.BaseDirectory, TestBlueprintsRelativePath);
            opts.IsEnabled = true;
        });
    }

    public IBlueprintService GetBlueprintService() => GetService<IBlueprintService>();

    public IBlueprintCatalogManager GetBlueprintCatalogManager() => GetService<IBlueprintCatalogManager>();

    public ITenantBlueprintHistory GetBlueprintHistory() => GetService<ITenantBlueprintHistory>();

    public ITenantBackupService GetBackupService() => GetService<ITenantBackupService>();

    public IRuntimeRepositoryProvider GetRuntimeRepositoryProvider() => GetService<IRuntimeRepositoryProvider>();

    /// <summary>
    /// Creates a fresh child tenant, imports the Test CK model into it, and
    /// returns the tenant id. Caller is responsible for invoking
    /// <see cref="DropTenantAsync"/> in a finally block.
    /// </summary>
    public async Task<string> CreateTestTenantAsync(string? prefix = null)
    {
        // The MongoDB driver caps ApplicationName at 128 UTF-8 bytes. The driver builds
        // it as "OctoMesh-{db}-{guid}-octo-system-ds-user-{db}" (= 67 + 2 * len(db) chars),
        // so the tenant id must stay well below ~30 chars. We use a short prefix + an
        // 8-char hex hash of a Guid for collision resistance.
        var shortHash = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"{prefix ?? "bp"}-{shortHash}";

        var systemContext = GetSystemContext();
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        // Load the Test CK model into the child tenant so seed-data referencing
        // Test/Customer can be imported. The blueprint's ckModelDependencies
        // list this same model, but the engine currently only triggers CK model
        // *migrations* — the initial import has to happen out-of-band.
        var childContext = await systemContext.FindTenantContextAsync(tenantId);
        var importResult = new OperationResult();
        await childContext.ImportCkModelAsync(new CkModelId("Test"), importResult);

        // Warm the in-memory CK cache for the tenant. Without this, the
        // engine's BlueprintService.ApplyBlueprintAsync calls
        // _ckCacheService.CreateTenant() (which only allocates the cache entry
        // but does not load the model graph) and downstream validation throws
        // CacheUnloaded.
        await ((TenantContext)childContext).LoadCacheForTenantAsync();

        return tenantId;
    }

    public async Task DropTenantAsync(string tenantId)
    {
        var systemContext = GetSystemContext();
        try
        {
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            await systemContext.DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }
        catch
        {
            // ignore cleanup failures — fixture-level cleanup is best-effort
        }
    }
}
