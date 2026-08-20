namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Fixture for <c>SystemTenantVirginBootstrapTests</c>.
/// </summary>
/// <remarks>
///     Derives from <see cref="DatabaseFixture" /> — NOT <see cref="SystemFixture" /> — so the
///     container starts without a system tenant: the tests reproduce the very first boot of a fresh
///     installation against a virgin MongoDB (AB#4854), where the order of infrastructure writes vs.
///     the system-tenant bootstrap is exactly what is under test. Its tests create and destroy the
///     system tenant at will, so this needs a container nobody else shares.
/// </remarks>
public class VirginSystemFixture : DatabaseFixture;
