using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="BlueprintServiceFixture" /> across every test class that joins it.
///     Replaces per-class <c>IClassFixture&lt;BlueprintServiceFixture&gt;</c>.
/// </summary>
[CollectionDefinition(Name)]
public class BlueprintServiceCollection : ICollectionFixture<BlueprintServiceFixture>
{
    public const string Name = "BlueprintService";
}
