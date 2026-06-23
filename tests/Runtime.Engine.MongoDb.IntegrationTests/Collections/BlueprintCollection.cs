using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="BlueprintFixture" /> across every test class that joins it.
///     Replaces per-class <c>IClassFixture&lt;BlueprintFixture&gt;</c>.
/// </summary>
[CollectionDefinition(Name)]
public class BlueprintCollection : ICollectionFixture<BlueprintFixture>
{
    public const string Name = "Blueprint";
}
