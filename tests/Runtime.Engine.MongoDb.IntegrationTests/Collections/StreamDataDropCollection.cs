using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>Shares one <see cref="StreamDataDropFixture" /> across the tenant-drop stream data tests.</summary>
[CollectionDefinition(Name)]
public class StreamDataDropCollection : ICollectionFixture<StreamDataDropFixture>
{
    public const string Name = "StreamDataDrop";
}

/// <summary>Shares one <see cref="StreamDataDisabledDropFixture" /> (instance flag off).</summary>
[CollectionDefinition(Name)]
public class StreamDataDisabledDropCollection : ICollectionFixture<StreamDataDisabledDropFixture>
{
    public const string Name = "StreamDataDisabledDrop";
}
