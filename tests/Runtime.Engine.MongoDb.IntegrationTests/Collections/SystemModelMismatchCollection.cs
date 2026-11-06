using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Isolated collection for the System-model version-mismatch diagnostic. See
///     <see cref="SystemModelMismatchFixture" /> for why it cannot share a fixture.
/// </summary>
[CollectionDefinition(Name)]
public class SystemModelMismatchCollection : ICollectionFixture<SystemModelMismatchFixture>
{
    public const string Name = "SystemModelMismatch";
}
