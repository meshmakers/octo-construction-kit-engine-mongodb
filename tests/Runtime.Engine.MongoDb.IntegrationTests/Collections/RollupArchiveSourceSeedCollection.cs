using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="RollupArchiveSourceSeedFixture" /> (stream data enabled, the shipped
///     rollup seeds ImportRt-ed once) across the AB#5157 seed-compatibility tests. Its own system
///     tenant keeps the rollup population deterministic for the source-counting assertions.
/// </summary>
[CollectionDefinition(Name)]
public class RollupArchiveSourceSeedCollection : ICollectionFixture<RollupArchiveSourceSeedFixture>
{
    public const string Name = "RollupArchiveSourceSeed";
}
