using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="StreamDataFlagFixture" /> (stream data enabled at instance level, model
///     descriptor registered, no CrateDB) across the stream data flag tests.
/// </summary>
[CollectionDefinition(Name)]
public class StreamDataFlagCollection : ICollectionFixture<StreamDataFlagFixture>
{
    public const string Name = "StreamDataFlag";
}
