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
    /// Most sub-batch statements (1,000 rows each) one archive insert runs at the same time (AB#8875). The rows are split
    /// into lanes by row key, so two statements in flight never carry the same key; each statement leases its own
    /// connection, so this is the share of the tenant's pool one large write may hold. Default 4. Bound from the key
    /// <see cref="MaxConcurrentInsertStatementsKey" /> (environment variable <c>OCTO_STREAMDATA__MAXCONCURRENTINSERTSTATEMENTS</c>)
    /// by both registration extensions, after the host's own configuration; no other property of this class is bound
    /// from configuration. Not positive = 1 (one statement after another); above <see cref="MaxPoolSize" /> =
    /// <see cref="MaxPoolSize" />.
    /// </summary>
    public int MaxConcurrentInsertStatements { get; set; } = 4;

    /// <summary>
    /// Configuration key of <see cref="MaxConcurrentInsertStatements" />.
    /// </summary>
    public const string MaxConcurrentInsertStatementsKey = "StreamData:MaxConcurrentInsertStatements";

    /// <summary>
    /// How long an idle pooled connection may live before the pool closes it. Keep slightly below
    /// any upstream proxy or load-balancer idle timeout to avoid using a connection the other
    /// side has already half-closed.
    /// </summary>
    public TimeSpan ConnectionIdleLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often the pool prunes connections that have exceeded <see cref="ConnectionIdleLifetime"/>.
    /// The pool samples idle connections on this cadence; a broken/half-closed idle connection is
    /// therefore evicted within roughly one pruning interval rather than lingering to be handed out
    /// and fail on next use. Keep well below <see cref="ConnectionIdleLifetime"/>.
    /// </summary>
    public TimeSpan ConnectionPruningInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// TCP keepalive interval for pooled physical connections. A positive value makes Npgsql send
    /// keepalive probes so a connection silently dropped by the CrateDB server or an intermediary
    /// (LB/proxy) is detected and torn down instead of being reused mid-backfill and surfacing as
    /// <c>"Exception while reading from stream"</c>. <see cref="TimeSpan.Zero"/> disables keepalives.
    /// </summary>
    public TimeSpan Keepalive { get; set; } = TimeSpan.FromSeconds(30);

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
    /// Bounded-retro-reach fleet ceiling (AB#4196), in milliseconds. A fleet-wide backstop on how
    /// far before the consumed watermark a single retroactive write may drag an <b>automatic</b>
    /// recompute of dependent rollups. The effective cap applied at detection is
    /// <c>min(Archive.MaxRetroactiveReachMs, this)</c> — a per-archive value can only tighten the
    /// ceiling, never loosen it. <c>null</c> (default) ⇒ no fleet ceiling, so behaviour is governed
    /// purely by the per-archive cap (and unbounded when that is also null, the pre-1.6.8 default).
    /// Bound to <c>StreamData:Recompute:MaxRetroactiveReachHardLimitMs</c>. Only bounds the automatic
    /// path; manual <c>recomputeArchive</c> / <c>rewindRollupWatermark</c> stay unbounded.
    /// </summary>
    public long? MaxRetroactiveReachHardLimitMs { get; set; }

    /// <summary>
    /// Width of the time slices a computed-column backfill walks the archive in (AB#8002). Every
    /// page query of the backfill is bounded to one slice of the archive's time key (raw:
    /// <c>timestamp</c>, windowed: <c>window_start</c>), so CrateDB filters and sorts only the rows of
    /// that slice instead of the whole remaining table, and the memory of a statement stays bounded
    /// on archives with a long history. Empty slices are skipped with one lookup. Default: 1 day.
    /// Bound to <c>StreamData:ComputedColumnBackfillSlice</c>. Must be positive.
    /// </summary>
    public TimeSpan ComputedColumnBackfillSlice { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// How long a downsampling query on a time-range archive waits for the window-phase probe (AB#8962, AB#8992)
    /// before it goes on with the unsnapped axis starting at <c>From</c>. The probe runs once, outside the
    /// resilience pipeline (no retry), and this bound covers the wait for a pooled connection as well. Default 5 s.
    /// Bound to <c>StreamData:DownsamplingPhaseProbeTimeout</c>. Not positive = 5 s.
    /// </summary>
    public TimeSpan DownsamplingPhaseProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the process remembers the window phase a probe found for one archive, window length and filter set
    /// (AB#8992), so repeated downsampling queries need no probe. Only a single phase is remembered. Default 5 min;
    /// <see cref="TimeSpan.Zero"/> switches the cache off (every query probes, bounded by
    /// <see cref="DownsamplingPhaseProbeTimeout"/>, and a probe never outlives its query). Bound to
    /// <c>StreamData:DownsamplingPhaseProbeCacheDuration</c>.
    /// </summary>
    public TimeSpan DownsamplingPhaseProbeCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

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