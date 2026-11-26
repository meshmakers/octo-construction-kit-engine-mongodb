using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     The test classes that change the process-wide static <c>ReverseNavigationOptions.EdgeThreshold</c>. xunit runs
///     a collection with <c>DisableParallelization</c> after all parallel collections and alone, so no association query of another collection runs while the threshold is lowered (it would route a sparse
///     origin to the dense paths). Own fixture instances (own system database) with the sample geography data and with
///     the swappable data-permission resolver.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ReverseNavigationThresholdCollection : ICollectionFixture<SampleRtModelDataFixture>,
    ICollectionFixture<DataPermissionTestFixture>
{
    public const string Name = "ReverseNavigationThreshold";
}
