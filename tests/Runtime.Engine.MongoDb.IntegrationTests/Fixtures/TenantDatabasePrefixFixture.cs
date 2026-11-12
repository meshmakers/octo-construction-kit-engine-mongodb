namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Fixture for the tenant-database prefix tests (AB#8797).
/// </summary>
/// <remarks>
///     Its own fixture (own system tenant) because the tests switch the prefix of the bound
///     <c>OctoSystemConfiguration</c> at run time to emulate a restart with a prefix; no other collection may see that.
/// </remarks>
public class TenantDatabasePrefixFixture : SystemFixture;
