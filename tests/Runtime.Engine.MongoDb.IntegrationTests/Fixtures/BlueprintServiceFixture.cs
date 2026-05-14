using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs.Serialization;
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

        var rootPath = Path.Combine(AppContext.BaseDirectory, TestBlueprintsRelativePath);
        var catalogV1 = Path.Combine(rootPath, "blueprints", "v1");

        // CI diagnostic: surface the directory layout immediately. If the
        // <Content Include> glob did not copy the test blueprints into
        // bin/<config>/<tfm>/TestBlueprints/, every blueprint test fails
        // with an opaque "Blueprint 'TestBp-1.0.0' could not be loaded from
        // any catalog" — the engine swallows the catalog's own error in
        // BlueprintResolutionConflict.AdditionalContext. Throwing here makes
        // the real cause visible in the test output.
        if (!Directory.Exists(catalogV1))
        {
            var baseContents = Directory.Exists(AppContext.BaseDirectory)
                ? string.Join(", ", Directory.EnumerateFileSystemEntries(AppContext.BaseDirectory)
                    .Select(Path.GetFileName).Take(50))
                : "(BaseDirectory does not exist)";
            var rootContents = Directory.Exists(rootPath)
                ? string.Join(", ", Directory.EnumerateFileSystemEntries(rootPath, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(rootPath, p)).Take(50))
                : "(TestBlueprints root missing)";
            throw new InvalidOperationException(
                $"TestBlueprints catalog directory not found at '{catalogV1}'. " +
                $"AppContext.BaseDirectory='{AppContext.BaseDirectory}'. " +
                $"BaseDirectory contents: {baseContents}. " +
                $"TestBlueprints contents: {rootContents}");
        }

        Services.Configure<LocalFileSystemBlueprintCatalogOptions>(opts =>
        {
            opts.RootPath = rootPath;
            opts.IsEnabled = true;
        });
    }

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        // Deeper diagnostic: the catalog's RefreshCatalogAsync silently swallows
        // any blueprint.yaml that fails to parse (`catch { /* Skip */ }`), so
        // even with the directory present, a serialiser or schema-validator
        // failure surfaces only as "Blueprint 'X' could not be loaded". Eagerly
        // deserialise every manifest under blueprints/v1/ via the same
        // BlueprintYamlSerializer the catalog uses, and throw with the actual
        // operation messages if any fail.
        var rootPath = Path.Combine(AppContext.BaseDirectory, TestBlueprintsRelativePath);
        var catalogV1 = Path.Combine(rootPath, "blueprints", "v1");
        var serializer = GetService<IBlueprintSerializer>();

        var manifestPaths = Directory.EnumerateFiles(catalogV1, "blueprint.yaml", SearchOption.AllDirectories).ToList();
        if (manifestPaths.Count == 0)
        {
            throw new InvalidOperationException(
                $"No blueprint.yaml files found under '{catalogV1}'. " +
                $"Subdirectories: {string.Join(", ", Directory.EnumerateDirectories(catalogV1).Select(Path.GetFileName))}");
        }

        var parseFailures = new List<string>();
        foreach (var manifestPath in manifestPaths)
        {
            await using var stream = File.OpenRead(manifestPath);
            var opResult = new OperationResult();
            try
            {
                var blueprint = await serializer.DeserializeBlueprintMetaAsync(stream, manifestPath, opResult);
                if (opResult.HasErrors || blueprint == null)
                {
                    parseFailures.Add(
                        $"{manifestPath}: {string.Join(" | ", opResult.Messages.Select(m => $"[{m.MessageLevel}] {m.MessageText}"))}");
                }
            }
            catch (Exception ex)
            {
                parseFailures.Add($"{manifestPath}: threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (parseFailures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Blueprint manifest(s) under '{catalogV1}' failed to deserialise: " +
                string.Join(" ;; ", parseFailures));
        }
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
