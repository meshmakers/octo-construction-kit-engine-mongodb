using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
/// Fixture for blueprint integration tests.
/// Extends SystemFixture to include MongoDB blueprint support.
/// </summary>
public class BlueprintFixture : SystemFixture
{
    public BlueprintFixture()
    {
        // Add MongoDB blueprint support which overrides in-memory implementations from AddRuntimeEngine()
        // Must be called after AddRuntimeEngine().AddMongoDbRuntimeRepository() to properly override
        Services.AddRuntimeEngine()
            .AddMongoBlueprintSupport();
    }

    /// <summary>
    /// Gets the blueprint history service from DI
    /// </summary>
    public ITenantBlueprintHistory GetBlueprintHistory()
    {
        return GetService<ITenantBlueprintHistory>();
    }

    /// <summary>
    /// Gets the backup service for blueprints from DI
    /// </summary>
    public ITenantBackupService GetBackupService()
    {
        return GetService<ITenantBackupService>();
    }

    /// <summary>
    /// Gets the tenant blueprint installations service from DI.
    /// </summary>
    public ITenantBlueprintInstallations GetBlueprintInstallations()
    {
        return GetService<ITenantBlueprintInstallations>();
    }
}
