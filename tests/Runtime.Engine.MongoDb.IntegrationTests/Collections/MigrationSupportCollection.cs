using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="MigrationSupportFixture" /> across every test class that joins it.
///     Replaces per-class <c>IClassFixture&lt;MigrationSupportFixture&gt;</c>.
/// </summary>
[CollectionDefinition(Name)]
public class MigrationSupportCollection : ICollectionFixture<MigrationSupportFixture>
{
    public const string Name = "MigrationSupport";
}
