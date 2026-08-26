namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Fixture for the tenant namespace guard tests (AB#4762 / AB#4763).
/// </summary>
/// <remarks>
///     Deliberately its own fixture rather than the shared <see cref="SystemFixture" />: these tests
///     deliberately drive the destructive create path, and against unfixed code they leave dropped
///     databases behind. Running them on the shared fixture would take out the system tenant that
///     roughly a dozen other test classes depend on. A separate fixture means a separate MongoDB
///     container, so the blast radius stays inside this collection.
/// </remarks>
public class TenantNamespaceFixture : SystemFixture;
