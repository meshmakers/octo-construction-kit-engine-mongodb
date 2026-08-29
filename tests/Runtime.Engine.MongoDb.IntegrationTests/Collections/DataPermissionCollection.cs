using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Shares one <see cref="DataPermissionTestFixture" /> (test CK model + swappable
///     data-permission resolver) across the enforcement test classes (AB#4973).
/// </summary>
[CollectionDefinition(Name)]
public class DataPermissionCollection : ICollectionFixture<DataPermissionTestFixture>
{
    public const string Name = "DataPermission";
}
