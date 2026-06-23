using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="SampleRtModelDataFixture" /> (test CK model + sample geography data
///     pre-seeded) across every test class that joins it. Replaces per-class
///     <c>IClassFixture&lt;SampleRtModelDataFixture&gt;</c>.
/// </summary>
[CollectionDefinition(Name)]
public class SampleRtModelDataCollection : ICollectionFixture<SampleRtModelDataFixture>
{
    public const string Name = "SampleRtModelData";
}
