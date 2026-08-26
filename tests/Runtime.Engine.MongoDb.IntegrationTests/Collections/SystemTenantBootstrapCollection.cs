using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Isolated collection for the system-tenant bootstrap guard. See
///     <see cref="SystemTenantBootstrapFixture" /> for why it cannot share a fixture.
/// </summary>
[CollectionDefinition(Name)]
public class SystemTenantBootstrapCollection : ICollectionFixture<SystemTenantBootstrapFixture>
{
    public const string Name = "SystemTenantBootstrap";
}
