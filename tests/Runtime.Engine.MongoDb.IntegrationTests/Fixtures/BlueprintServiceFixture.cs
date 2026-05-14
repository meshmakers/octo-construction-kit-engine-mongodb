using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Engine.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

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
        Services.AddRuntimeEngine()
            .AddMongoBlueprintSupport();

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
        var tenantId = $"{prefix ?? "blueprint-test"}-{Guid.NewGuid():N}";

        var systemContext = GetSystemContext();
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

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
