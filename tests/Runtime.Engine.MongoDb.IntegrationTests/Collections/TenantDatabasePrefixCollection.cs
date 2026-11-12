using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Isolated collection for the tenant-database prefix tests. See <see cref="TenantDatabasePrefixFixture" />.
/// </summary>
[CollectionDefinition(Name)]
public class TenantDatabasePrefixCollection : ICollectionFixture<TenantDatabasePrefixFixture>
{
    public const string Name = "TenantDatabasePrefix";
}
