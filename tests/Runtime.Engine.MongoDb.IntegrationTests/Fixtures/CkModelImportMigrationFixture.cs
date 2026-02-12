using Meshmakers.Octo.Runtime.Contracts.MongoDb;

using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
/// Fixture for CK model import migration tests.
/// Registers both TestCkModel v1 and v2 to enable migration testing.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class CkModelImportMigrationFixture : SystemFixture
{
    public CkModelImportMigrationFixture()
    {
        // Register TestCkModel v2 in addition to v1 (already registered in base)
        Services.AddCkModelTestV2();
    }

    /// <summary>
    /// Resets the system tenant to a clean state (no imported CK models).
    /// Call at the start of each test to ensure test isolation.
    /// </summary>
    public async Task ResetTenantAsync()
    {
        var systemContext = GetSystemContext();
        await systemContext.ClearSystemTenantAsync();
    }
}
