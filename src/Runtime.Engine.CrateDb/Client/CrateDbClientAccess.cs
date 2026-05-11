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
/// Per-tenant <see cref="NpgsqlDataSource"/> cache: a lock-free
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by tenant id, with each entry wrapped in
/// a <see cref="Lazy{T}"/> so concurrent first-access requests share a single data-source build
/// instead of racing to construct duplicates.
///
/// <para>Pooling is intentionally disabled (<c>Pooling = false</c>) as a workaround for CrateDB
/// rejecting the <c>ROLLBACK</c> Npgsql sends during connector reset (concept §8 T14). The cache
/// therefore primarily saves the <c>NpgsqlDataSourceBuilder</c> setup cost rather than reducing
/// socket usage; once a future Npgsql/CrateDB combination allows pool resets, the same shape works
/// and the data source becomes the pool root with no API change.</para>
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
