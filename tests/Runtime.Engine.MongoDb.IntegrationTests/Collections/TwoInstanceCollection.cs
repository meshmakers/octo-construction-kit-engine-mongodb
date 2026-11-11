using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Isolated collection for the cross-instance ownership tests (AB#8796). See
///     <see cref="TwoInstanceFixture" />.
/// </summary>
[CollectionDefinition(Name)]
public class TwoInstanceCollection : ICollectionFixture<TwoInstanceFixture>
{
    public const string Name = "TwoInstance";
}
