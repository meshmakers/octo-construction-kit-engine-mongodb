namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;

/// <summary>
/// Configuration for the stream data database.
/// </summary>
public class StreamDataConfiguration
{
    /// <summary>
    /// Connection string for the stream data database.
    /// </summary>
    public required string ConnectionString { get; set; }

    /// <summary>
    /// Sliding lifetime for cached <c>NpgsqlDataSource</c> entries. After T1 (pooling rework) the
    /// data source IS the connection pool, so keeping it alive longer keeps the pool warm. The
    /// default mirrors the value used before pooling was enabled.
    /// </summary>
    public TimeSpan ConnectionCacheDuration { get; set; } = Constants.DefaultConnectionCacheDuration;

    /// <summary>
    /// Minimum number of physical connections kept open per tenant data source. Set above zero to
    /// pre-warm the pool and absorb burst traffic without paying connect-handshake latency on the
    /// first hit.
    /// </summary>
    public int MinPoolSize { get; set; } = 0;

    /// <summary>
    /// Maximum number of physical connections per tenant data source. CrateDB clusters typically
    /// tolerate hundreds of pooled connections; the default is conservative — tune upward when
    /// stream data writes become a bottleneck.
    /// </summary>
    public int MaxPoolSize { get; set; } = 32;

    /// <summary>
    /// How long an idle pooled connection may live before the pool closes it. Keep slightly below
    /// any upstream proxy or load-balancer idle timeout to avoid using a connection the other
    /// side has already half-closed.
    /// </summary>
    public TimeSpan ConnectionIdleLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Master switch for Npgsql pooling. <c>true</c> by default after T1; set to <c>false</c> only
    /// when an environment-specific issue forces unpooled fallback (e.g., a CrateDB version that
    /// regresses on PostgreSQL session reset).
    /// </summary>
    public bool PoolingEnabled { get; set; } = true;

    /// <summary>
    /// Number of shards for CrateDB tables. Default is 3 for production clusters.
    /// Use 1 for single-node test environments.
    /// </summary>
    public int NumberOfShards { get; set; } = 3;

    /// <summary>
    /// Number of replicas for CrateDB tables. Default is -1 (CrateDB auto-config).
    /// Use 0 for single-node test environments.
    /// </summary>
    public int NumberOfReplicas { get; set; } = -1;

    /// <summary>
    /// Helper method to build a connection string from individual settings. Omits the
    /// <c>Password</c> token entirely when no password is supplied so callers don't end up with a
    /// stray <c>Password=;</c> on the wire.
    /// </summary>
    public void ConnectionStringFromConfiguration(string host, string user, string? password)
    {
        ConnectionString = string.IsNullOrEmpty(password)
            ? $"Host={host};Username={user};SSL Mode=Prefer"
            : $"Host={host};Username={user};Password={password};SSL Mode=Prefer";
    }

}