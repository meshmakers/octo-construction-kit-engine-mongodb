using System;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#8266: an exhausted per-tenant connection pool fails fast with its own exception instead of being retried by the
/// resilience pipeline until the call timeout ends it as a misleading 35 s statement timeout; and the driver internals the
/// client uses to discard a connection it cannot return to the pool still exist.
/// </summary>
public class CrateDatabaseClientConnectionPoolTests
{
    private const string PoolExhaustedMessage =
        "The connection pool has been exhausted, either raise 'Max Pool Size' (currently 32) or 'Timeout' (currently 15 seconds) in your connection string.";

    [Fact]
    public void ThePoolExhaustedError_IsRecognised() =>
        Assert.True(CrateDatabaseClient.IsConnectionPoolExhausted(new NpgsqlException(PoolExhaustedMessage, new TimeoutException())));

    [Fact]
    public void AConnectTimeout_IsNotPoolExhaustion() =>
        Assert.False(CrateDatabaseClient.IsConnectionPoolExhausted(
            new NpgsqlException("Exception while connecting", new TimeoutException("Timeout during connecting"))));

    [Fact]
    public void ThePoolText_WithoutAnInnerTimeout_IsNotPoolExhaustion() =>
        Assert.False(CrateDatabaseClient.IsConnectionPoolExhausted(new NpgsqlException(PoolExhaustedMessage)));

    [Fact]
    public void TheDriverInternalsToDiscardAConnection_Exist()
    {
        // Fails when an Npgsql update renames NpgsqlConnection.Connector or NpgsqlConnector.Break(Exception, bool).
        // Without them a connection that cannot be brought back to idle would keep its pool slot for the process lifetime.
        Assert.True(CrateDatabaseClient.CanDiscardConnections);
    }

    [Fact]
    public async Task AnExhaustedPool_FailsAtOnce_WithoutRetry()
    {
        var access = new ExhaustedPoolAccess();
        var client = CreateClient(access);

        var error = await Assert.ThrowsAsync<StreamDataConnectionPoolExhaustedException>(
            () => client.ExecuteNonQueryAsync("tenanta", "DROP TABLE IF EXISTS doc.t", CancellationToken.None));

        Assert.Equal(1, access.Calls);
        Assert.Equal("tenanta", error.TenantId);
        Assert.IsType<NpgsqlException>(error.InnerException);
        Assert.Contains("tenanta", error.Message, StringComparison.Ordinal);
        Assert.Contains("exhausted", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExhaustedPool_DoesNotOpenTheSharedCircuitBreaker()
    {
        // One tenant's full pool must not cut every tenant off: the breaker counts connection failures only.
        var access = new ExhaustedPoolAccess();
        var client = CreateClient(access);

        for (var i = 0; i < 20; i++)
        {
            await Assert.ThrowsAsync<StreamDataConnectionPoolExhaustedException>(
                () => client.ExecuteNonQueryAsync("tenanta", "SELECT 1", CancellationToken.None));
        }

        Assert.Equal(20, access.Calls);
    }

    private static CrateDatabaseClient CreateClient(ICrateDbConnectionAccess access)
    {
        var configuration = Options.Create(new StreamDataConfiguration { ConnectionString = "Host=localhost;Username=crate" });
        return new CrateDatabaseClient(
            NullLogger<CrateDatabaseClient>.Instance,
            access,
            configuration,
            Options.Create(new CrateResilienceOptions { BaseRetryDelay = TimeSpan.FromMilliseconds(1), CircuitMinimumThroughput = 2 }),
            Options.Create(new StreamDataInstanceConfiguration()),
            Options.Create(new CrateRecomputeOptions()));
    }

    private sealed class ExhaustedPoolAccess : ICrateDbConnectionAccess
    {
        public int Calls { get; private set; }

        public Task<NpgsqlConnection> CreateConnectionAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new NpgsqlException(PoolExhaustedMessage, new TimeoutException());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
