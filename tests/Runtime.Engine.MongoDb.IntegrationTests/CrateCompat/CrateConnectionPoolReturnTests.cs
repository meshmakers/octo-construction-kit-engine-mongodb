using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CrateCompat;

/// <summary>
///     AB#8266: a failed statement must give its pooled connection back. CrateDB reports the failed-transaction state after
///     every failed statement, Npgsql then sends a ROLLBACK when it resets the connection, CrateDB rejects it, and the
///     driver's close ended before the connection went back to the pool: every failed statement cost one slot of the
///     tenant's pool until it was exhausted (test-2 <c>scratch-eiq-adopt-a</c>: rollups over missing tables, 32 slots
///     gone a minute after each start, then every statement of the tenant, a <c>DROP TABLE</c> too, ended after 35 s).
///     Runs on every supported CrateDB line (<c>OCTO_TEST_CRATEDB_IMAGE</c>).
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class CrateConnectionPoolReturnTests(CrateStagingCopyFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FailedStatements_ReturnTheirConnectionsToThePool()
    {
        await using var access = NewAccess(maxPoolSize: 2);
        var client = NewClient(access);
        var tenantId = CrateStagingCopyFixture.NewTenantId();
        var existing = TenantSchema.QualifiedArchiveTable(tenantId, OctoObjectId.GenerateNewId().ToString());
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(
            existing, [new ArchiveColumnDdl("v", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, true)], 1, 0));
        var missing = TenantSchema.QualifiedArchiveTable(tenantId, OctoObjectId.GenerateNewId().ToString());

        // More failures than the pool has slots: before the fix the third statement already waited for a free slot.
        for (var i = 0; i < 5; i++)
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => client.GetCountAsync(tenantId, $"SELECT COUNT(*) FROM {missing}", Ct));
            Assert.IsNotType<StreamDataConnectionPoolExhaustedException>(error);
            Assert.True(CrateDbStreamDataRepository.IsMissingTableError(error), error.ToString());
        }

        Assert.Equal(0, await client.GetCountAsync(tenantId, $"SELECT COUNT(*) FROM {existing}", Ct));
        await client.ExecuteDdlAsync(tenantId, $"DROP TABLE IF EXISTS {missing}", Ct);
        await client.ExecuteDdlAsync(tenantId, $"DROP TABLE IF EXISTS {existing}", Ct);
    }

    [Fact]
    public async Task FailedWrites_ReturnTheirConnectionsToThePool()
    {
        await using var access = NewAccess(maxPoolSize: 2);
        var client = NewClient(access);
        var tenantId = CrateStagingCopyFixture.NewTenantId();
        var missing = TenantSchema.QualifiedArchiveTable(tenantId, OctoObjectId.GenerateNewId().ToString());

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => client.ExecuteNonQueryAsync(
                tenantId, $"DELETE FROM {missing} WHERE \"rtid\" = 'x'", CrateStatementClass.Maintenance, Ct));
        }

        await client.ExecuteDdlAsync(tenantId, $"DROP TABLE IF EXISTS {missing}", Ct);
    }

    [Fact]
    public async Task ADiscardedConnection_FreesItsPoolSlot()
    {
        // The last resort of the dispose path: a connection that cannot be brought back to idle is broken, so the pool
        // drops it instead of keeping its slot.
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Pooling = true, MaxPoolSize = 1, Timeout = 3, NoResetOnClose = true,
        };
        await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);

        var first = await dataSource.OpenConnectionAsync(Ct);
        Assert.True(CrateDatabaseClient.DiscardConnection(first, new InvalidOperationException("test")));
        await first.DisposeAsync();

        await using var second = await dataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT 1", second);
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture));
    }

    private CrateDbConnectionAccess NewAccess(int maxPoolSize)
    {
        // A short wait for a pool slot, so a leak fails the test in seconds instead of after 15 s per attempt.
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Timeout = 3 }.ConnectionString;
        var configuration = Options.Create(new StreamDataConfiguration
        {
            ConnectionString = connectionString,
            PoolingEnabled = true,
            MaxPoolSize = maxPoolSize,
        });
        return new CrateDbConnectionAccess(configuration, NullLogger<CrateDbConnectionAccess>.Instance);
    }

    private static CrateDatabaseClient NewClient(CrateDbConnectionAccess access)
    {
        var configuration = Options.Create(new StreamDataConfiguration { ConnectionString = "unused" });
        return new CrateDatabaseClient(
            NullLogger<CrateDatabaseClient>.Instance,
            access,
            configuration,
            Options.Create(new CrateResilienceOptions()),
            Options.Create(new StreamDataInstanceConfiguration()),
            Options.Create(new CrateRecomputeOptions()));
    }
}
