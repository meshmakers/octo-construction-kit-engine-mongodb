using Xunit;

using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

/// <summary>
///     Collection that shares a single <see cref="SystemFixture" /> (and therefore one MongoDB
///     Testcontainer) across every test class that joins it. Replaces per-class
///     <c>IClassFixture&lt;SystemFixture&gt;</c> instantiation, which used to start one container
///     per class and dominated the CI integration-test wall-clock time.
/// </summary>
[CollectionDefinition(Name)]
public class SystemCollection : ICollectionFixture<SystemFixture>
{
    public const string Name = "System";
}
