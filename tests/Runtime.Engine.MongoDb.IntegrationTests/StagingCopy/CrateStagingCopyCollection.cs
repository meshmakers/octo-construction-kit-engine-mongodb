using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     One <see cref="CrateStagingCopyFixture" /> (one CrateDB container) for every staging copy test; the tests
///     of a collection run serially, which they need because some change a cluster setting.
/// </summary>
[CollectionDefinition(Name)]
public class CrateStagingCopyCollection : ICollectionFixture<CrateStagingCopyFixture>
{
    public const string Name = "CrateStagingCopy";
}
