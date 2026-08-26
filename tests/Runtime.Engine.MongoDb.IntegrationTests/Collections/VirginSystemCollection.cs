using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Isolated collection for the virgin-server bootstrap tests. See
///     <see cref="VirginSystemFixture" /> for why it cannot share a fixture.
/// </summary>
[CollectionDefinition(Name)]
public class VirginSystemCollection : ICollectionFixture<VirginSystemFixture>
{
    public const string Name = "VirginSystem";
}
