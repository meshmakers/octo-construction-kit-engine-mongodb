using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
/// <see cref="BlueprintServiceFixture" /> with stream data on: the system tenant holds System.StreamData, and a child
/// tenant gets it through <see cref="EnableStreamDataAsync" />.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class ArchiveDefinitionGuardFixture : BlueprintServiceFixture
{
    public ArchiveDefinitionGuardFixture()
    {
        Services.Configure<StreamDataInstanceConfiguration>(c => c.Enabled = true);
        Services.AddSingleton<IStreamDataCkModelDescriptor>(
            _ => new StreamDataCkModelDescriptor(SystemStreamDataCkIds.CkModelId));
    }

    /// <summary>The system tenant, with stream data enabled.</summary>
    public ITenantContext TenantContext { get; private set; } = null!;

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        TenantContext = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        await TenantContext.EnableStreamDataAsync();
    }

    /// <summary>Enables stream data on a child tenant.</summary>
    public async Task<ITenantContext> EnableStreamDataAsync(string tenantId)
    {
        var tenant = await GetSystemContext().FindTenantContextAsync(tenantId);
        await tenant.EnableStreamDataAsync();
        return tenant;
    }
}
