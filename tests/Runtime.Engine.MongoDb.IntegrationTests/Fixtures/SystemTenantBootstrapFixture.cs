namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Fixture for <c>SystemTenantBootstrapGuardTests</c>.
/// </summary>
/// <remarks>
///     Its test deliberately breaks its own system tenant (removes the System CK model while keeping
///     the database) to reproduce the state in which the identity service used to drop the whole
///     platform database at startup (AB#4762). That leaves the system tenant unusable afterwards, so
///     this needs a container nobody else shares.
/// </remarks>
public class SystemTenantBootstrapFixture : SystemFixture;
