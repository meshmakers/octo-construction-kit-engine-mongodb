using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.InterfaceQuery;

/// <summary>
///     System tenant with the interface query test model and a swappable data-permission resolver (AB#7019).
///     <see cref="ResetAsync" /> drops all data and re-imports the model, so every test starts from an empty tenant.
/// </summary>
public class InterfaceQueryFixture : SystemFixture
{
    public InterfaceQueryFixture()
    {
        // Last registration wins over the TryAddSingleton in AddRuntimeEngine.
        Services.AddSingleton<IDataPermissionResolver>(Resolver);
    }

    public TestDataPermissionResolver Resolver { get; } = new();

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();
        await ImportModelAsync();
    }

    public async Task ResetAsync()
    {
        Resolver.Table = RtDataPolicyTable.Empty;
        await GetSystemContext().ClearSystemTenantAsync();
        await ImportModelAsync();
    }

    public async Task<CkModelId> GetInstalledSystemIdAsync()
    {
        var systemContext = GetSystemContext();
        var catalogService = GetService<ICatalogService>();
        CkModelId? systemId = null;
        foreach (var version in (await catalogService.ListVersionsAsync("System")).OrderByDescending(v => v.ModelId))
        {
            if (await systemContext.IsCkModelExistingAsync(version.ModelId))
            {
                systemId = version.ModelId;
                break;
            }
        }

        return systemId ?? throw new InvalidOperationException("No installed System model found.");
    }

    private async Task ImportModelAsync()
    {
        await GetSystemContext().ImportCkModelAsync(InterfaceQueryTestModel.Build(await GetInstalledSystemIdAsync()));
    }
}
