using Meshmakers.Octo.Runtime.Contracts.MongoDb;

using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
/// Fixture for CK model import migration tests.
/// Registers both TestCkModel v1 and v2 to enable migration testing.
/// </summary>
public class CkModelImportMigrationFixture : SystemFixture
{
    public CkModelImportMigrationFixture()
    {
        // Register TestCkModel v2 in addition to v1 (already registered in base)
        Services.AddCkModelTestV2();
    }
}
