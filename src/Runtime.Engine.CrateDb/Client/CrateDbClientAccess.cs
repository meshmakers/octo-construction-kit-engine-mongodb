using System.Collections.Concurrent;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

internal interface ICrateDbConnectionAccess : IAsyncDisposable
{
    /// <summary>
    /// Opens a pooled connection to CrateDB for the given tenant. Honors cancellation.
    /// </summary>
    Task<NpgsqlConnection> CreateConnectionAsync(string tenantId, CancellationToken cancellationToken = default);
}

/// <remarks>
/// Connection-pooling rework (concept §8 T1). Each tenant has one long-lived
/// <see cref="NpgsqlDataSource"/>; the data source is the pool root and reuses physical
/// connections across calls. The cache itself is a lock-free
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by tenant id, with each entry wrapped in
/// a <see cref="Lazy{T}"/> so concurrent first-access requests share the single build instead of
/// racing to construct duplicate data sources.
///
/// <para>CrateDB still rejects the <c>ROLLBACK</c> Npgsql sends when resetting a connector that
/// believes it is in a transaction. We pin <c>NoResetOnClose = true</c> and never start
/// transactions on these connections, so the reset path stays in the no-op branch and pooling is
/// safe.</para>
///
/// <para>Disposal: the access object is registered as a singleton; <see cref="DisposeAsync"/> tears
/// down every cached data source so the host shuts down cleanly without leaking sockets. Tenant
/// removal at runtime is not modeled here — tenants are stable for the process lifetime in the
/// services that consume this. If that changes, surface a per-tenant <c>EvictAsync</c>.</para>
/// </remarks>
internal class CrateDbConnectionAccess(
    IOptions<StreamDataConfiguration> options,
    ILogger<CrateDbConnectionAccess> logger)
    : ICrateDbConnectionAccess
{
    private readonly ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> _dataSources = new();
    private int _disposed;

    public Task<NpgsqlConnection> CreateConnectionAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(CrateDbConnectionAccess));
        }

        var key = NormalizeTenantId(tenantId);
        var dataSource = _dataSources.GetOrAdd(key, k => new Lazy<NpgsqlDataSource>(
            () => BuildDataSource(k),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        return dataSource.OpenConnectionAsync(cancellationToken).AsTask();
    }

    private NpgsqlDataSource BuildDataSource(string tenantId)
    {
        var config = options.Value;
        var datasourceId = Guid.NewGuid();

        logger.LogInformation(
            "Building CrateDB datasource '{DatasourceId}' for tenant '{TenantId}' (pooling=false; CrateDB protocol mismatch with Npgsql connector reset)",
            datasourceId, tenantId);
        logger.LogDebug("Connection string: {ConnectionString}", config.ConnectionString);

        var csb = new NpgsqlConnectionStringBuilder(config.ConnectionString)
        {
            // CrateDB rejects ROLLBACK that Npgsql sends during connector reset on connection
            // close. NoResetOnClose only suppresses DISCARD ALL — the rollback path inside
            // Npgsql.Internal.NpgsqlConnector.Reset() still fires when the connector's tracked
            // TransactionStatus is anything other than Idle, which CrateDB's Postgres protocol
            // implementation can leave it in even after a successful auto-commit INSERT (observed
            // on Npgsql 10.x against CrateDB 5.x). With Pooling disabled, NpgsqlConnection.Close()
            // physically closes the socket instead of going through the pool reset path, so the
            // ROLLBACK is never sent. NoResetOnClose stays true belt-and-braces in case the pool
            // is ever re-enabled by config in a future Npgsql/CrateDB combination.
            //
            // Trade-off: every CrateDB call pays a fresh TCP/TLS handshake + auth round-trip.
            // Acceptable for the per-archive insert paths the engine drives (poll-rate event
            // streams, batched ingest); revisit with a custom connector-reset hook if a future
            // hot path needs sub-millisecond CrateDB latency.
            NoResetOnClose = true,
            Pooling = false,
            ConnectionIdleLifetime = (int)Math.Max(1, config.ConnectionIdleLifetime.TotalSeconds)
        };

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        dataSourceBuilder.EnableDynamicJson([typeof(IReadOnlyDictionary<string, object?>)]);
        return dataSourceBuilder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (var entry in _dataSources)
        {
            if (!entry.Value.IsValueCreated) continue;

            try
            {
                await entry.Value.Value.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispose CrateDB datasource for tenant '{TenantId}'", entry.Key);
            }
        }

        _dataSources.Clear();
    }

    private static string NormalizeTenantId(string tenantId) => tenantId.ToLowerInvariant();
}
