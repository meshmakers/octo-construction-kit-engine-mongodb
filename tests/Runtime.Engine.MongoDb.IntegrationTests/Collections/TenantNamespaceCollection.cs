using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Isolated collection for the tenant namespace guard tests. See
///     <see cref="TenantNamespaceFixture" /> for why these must not share the System collection.
/// </summary>
[CollectionDefinition(Name)]
public class TenantNamespaceCollection : ICollectionFixture<TenantNamespaceFixture>
{
    public const string Name = "TenantNamespace";
}
