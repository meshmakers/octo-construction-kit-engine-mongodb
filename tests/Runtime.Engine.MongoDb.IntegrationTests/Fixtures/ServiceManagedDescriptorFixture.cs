using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
/// Fixture for the generic service-managed CK-model descriptor tests. Registers TestCkModel v2
/// (v1 is registered in the base) plus an <see cref="IServiceManagedCkModelDescriptor" /> pointing
/// at Test-2.0.0, so a tenant resolve auto-imports the embedded (v2) version.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class ServiceManagedDescriptorFixture : SystemFixture
{
    /// <summary>The embedded version the registered descriptor advertises.</summary>
    public static readonly CkModelId DescriptorModelId = new("Test-2.0.0");

    public ServiceManagedDescriptorFixture()
    {
        Services.AddCkModelTestV2();
        Services.AddSingleton<IServiceManagedCkModelDescriptor>(
            _ => new ServiceManagedCkModelDescriptor(DescriptorModelId));
    }

    /// <summary>Resets the system tenant to a clean state (no imported CK models).</summary>
    public async Task ResetTenantAsync()
    {
        var systemContext = GetSystemContext();
        await systemContext.ClearSystemTenantAsync();
    }
}
