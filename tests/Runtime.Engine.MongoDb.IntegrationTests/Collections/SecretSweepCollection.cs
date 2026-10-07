using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="SecretSweepFixture" /> (test CK model imported, key ring configured).
/// </summary>
[CollectionDefinition(Name)]
public class SecretSweepCollection : ICollectionFixture<SecretSweepFixture>
{
    public const string Name = "SecretSweep";
}
