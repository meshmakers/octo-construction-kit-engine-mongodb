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
/// <para>Pooling (AB#4278): honours <see cref="StreamDataConfiguration.PoolingEnabled"/> (default
/// on). When pooling is on the data source IS the connection pool and a long multi-chunk backfill
/// reuses healthy physical connections instead of paying a fresh TCP/TLS handshake per statement
/// (the unpooled shape churned connections and, under a decade-long recompute, dropped mid-run with
/// <c>"Exception while reading from stream"</c>). The pooled shape sets <c>No Reset On Close</c>,
/// which suppresses the <c>DISCARD ALL</c>/reset that CrateDB's Postgres-protocol implementation
/// rejects — that reset-on-return, not pooling itself, was the reason the original workaround
/// disabled the pool. Idle/broken connections are pruned via
/// <see cref="StreamDataConfiguration.ConnectionIdleLifetime"/> +
/// <see cref="StreamDataConfiguration.ConnectionPruningInterval"/> and probed via
/// <see cref="StreamDataConfiguration.Keepalive"/> so a silently-dropped connection is discarded
/// rather than handed out. Setting <c>PoolingEnabled=false</c> is the escape hatch that reproduces
/// the pre-AB#4278 unpooled behaviour (fresh connection per operation).</para>
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

        if (config.PoolingEnabled)
        {
            logger.LogInformation(
                "Building CrateDB datasource '{DatasourceId}' for tenant '{TenantId}' (pooling=true, No Reset On Close=true; " +
                "max pool {MaxPoolSize}, idle lifetime {IdleLifetimeSeconds}s, prune {PruningSeconds}s, keepalive {KeepaliveSeconds}s)",
                datasourceId, tenantId,
                config.MaxPoolSize,
                (int)Math.Max(1, config.ConnectionIdleLifetime.TotalSeconds),
                (int)Math.Max(1, config.ConnectionPruningInterval.TotalSeconds),
                (int)Math.Max(0, config.Keepalive.TotalSeconds));
        }
        else
        {
            logger.LogInformation(
                "Building CrateDB datasource '{DatasourceId}' for tenant '{TenantId}' " +
                "(pooling=false escape hatch; fresh connection per operation)",
                datasourceId, tenantId);
        }

        logger.LogDebug("Connection string: {ConnectionString}", config.ConnectionString);

        var connectionString = BuildConnectionString(config);

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson([typeof(IReadOnlyDictionary<string, object?>)]);
        return dataSourceBuilder.Build();
    }

    /// <summary>
    /// Builds the effective Npgsql connection string from <see cref="StreamDataConfiguration"/>,
    /// applying the pooling shape. Extracted (and internal) so it can be unit-tested without opening
    /// a real socket. AB#4278.
    /// </summary>
    /// <remarks>
    /// <para><b>Pooling on (default).</b> The data source becomes the connection pool. <c>No Reset On
    /// Close</c> is set so Npgsql does NOT send the <c>DISCARD ALL</c>/session-reset on connection
    /// return — CrateDB's Postgres-protocol implementation rejects that reset, and it (not pooling)
    /// was the root cause of the original <c>Pooling=false</c> workaround. With the reset suppressed,
    /// a returned connection is reused verbatim, so a long backfill keeps warm sockets instead of
    /// churning a fresh TCP/TLS handshake per statement. <c>Max/Min Pool Size</c>,
    /// <c>Connection Idle Lifetime</c>, <c>Connection Pruning Interval</c> and <c>Keepalive</c> tune
    /// pool size and ensure idle/broken connections are pruned/probed and healthy ones reused.</para>
    /// <para><b>Pooling off (escape hatch).</b> Reproduces the pre-AB#4278 behaviour: a brand-new
    /// physical connection per operation, closed on dispose. <c>No Reset On Close</c> stays set
    /// belt-and-braces; the pool-tuning keys are irrelevant when unpooled and are omitted.</para>
    /// </remarks>
    internal static string BuildConnectionString(StreamDataConfiguration config)
    {
        var idleLifetimeSeconds = (int)Math.Max(1, config.ConnectionIdleLifetime.TotalSeconds);

        var csb = new NpgsqlConnectionStringBuilder(config.ConnectionString)
        {
            // Suppress DISCARD ALL / session reset that CrateDB's Postgres protocol rejects. This is
            // what makes pooling safe against CrateDB — the reset-on-return, not pooling itself, was
            // the reason the original workaround disabled the pool. Kept true unconditionally so the
            // escape-hatch (unpooled) path stays belt-and-braces safe too.
            NoResetOnClose = true,
            Pooling = config.PoolingEnabled,
            ConnectionIdleLifetime = idleLifetimeSeconds,
        };

        if (config.PoolingEnabled)
        {
            csb.MaxPoolSize = Math.Max(1, config.MaxPoolSize);
            csb.MinPoolSize = Math.Max(0, config.MinPoolSize);
            // Prune idle connections on a tight cadence so a half-closed/broken connection is evicted
            // within ~one interval instead of being handed out and failing on next use.
            csb.ConnectionPruningInterval = (int)Math.Max(1, config.ConnectionPruningInterval.TotalSeconds);
            // TCP keepalive probes so a silently-dropped connection is detected and torn down rather
            // than reused mid-backfill (0 disables).
            csb.KeepAlive = (int)Math.Max(0, config.Keepalive.TotalSeconds);
        }

        return csb.ConnectionString;
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
