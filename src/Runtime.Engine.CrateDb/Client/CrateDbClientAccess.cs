using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

internal interface ICrateDbConnectionAccess
{
    /// <summary>
    /// Returns a connection to the crate db.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    NpgsqlConnection CreateConnection(string tenantId);
}

/// <remarks>
/// Concept §8 T14 — connection-cache decision after T4 (schema-per-tenant) and pending T12 (pooling rework):
/// <list type="bullet">
///   <item>The cache currently keeps a long-lived <see cref="NpgsqlDataSource"/> per tenant
///         under a 5-min sliding expiration. The data source itself is the unit of disposal —
///         on eviction we call <c>Dispose</c> in <see cref="DisposeCallback"/> so any connections
///         it holds are released promptly even with pooling currently disabled.</item>
///   <item>With <c>Pooling = false</c> (CrateDB ROLLBACK workaround) the data source does not
///         retain physical connections, so the cache primarily saves the
///         <c>NpgsqlDataSourceBuilder</c> setup cost rather than reducing socket usage.</item>
///   <item>Decision: keep the cache. Once T12 enables real pooling, the same shape works
///         (the data source becomes the pool root) and the sliding expiration becomes the
///         pool-eviction window. No behaviour change in this commit.</item>
/// </list>
/// </remarks>
internal class CrateDbConnectionAccess(
    IOptions<StreamDataConfiguration> options,
    ILogger<CrateDbConnectionAccess> logger)
    : ICrateDbConnectionAccess
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public NpgsqlConnection CreateConnection(string tenantId)
    {
        var cacheKey = NormalizeTenantId(tenantId);
        _semaphore.Wait();

        var dataSource = _cache.GetOrCreate<NpgsqlDataSource>(cacheKey, f =>
        {
            var datasourceId = Guid.NewGuid();

            f.SlidingExpiration = options.Value.ConnectionCacheDuration;
            f.RegisterPostEvictionCallback(DisposeCallback, datasourceId);

            var connectionString = options.Value.ConnectionString;

            logger.LogInformation("Creating database datasource '{DatasourceId}' for tenant {TenantId}",
                datasourceId,
                tenantId);
            logger.LogDebug("Connection string: {ConnectionString}", connectionString);

            var csb = new NpgsqlConnectionStringBuilder(connectionString)
            {
                // CrateDB does not support ROLLBACK which Npgsql sends during pooled connection
                // reset (NpgsqlConnector.Reset). NoResetOnClose only prevents DISCARD ALL but NOT
                // the ROLLBACK that Npgsql unconditionally sends when TransactionStatus != Idle.
                // Disabling pooling prevents Reset from being called entirely on connection close.
                NoResetOnClose = true,
                Pooling = false
            };
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
            dataSourceBuilder.EnableDynamicJson([typeof(IReadOnlyDictionary<string, object?>)]);
            var dataSource = dataSourceBuilder.Build();

            return dataSource;
        });
        
        _semaphore.Release();
        
        if (dataSource == null)
        {
            throw StreamDataException.CouldNotCreateDatabaseConnection();
        }

        return dataSource.OpenConnection();
    }

    private void DisposeCallback(object key, object? value, EvictionReason reason, object? state)
    {
        if (state is not Guid datasourceId || value is not NpgsqlDataSource datasource)
        {
            throw new InvalidOperationException("Invalid state");
        }

        logger.LogInformation("Disposing datasource '{DatasourceId}' for tenant '{TenantId}'", 
            datasourceId,
            key);

        datasource.Dispose();
    }

    private static string NormalizeTenantId(string tenantId) => tenantId.ToLowerInvariant();
}