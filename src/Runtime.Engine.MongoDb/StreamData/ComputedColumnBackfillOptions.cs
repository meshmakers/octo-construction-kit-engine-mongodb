using System;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// Configuration for the <see cref="ComputedColumnBackfillHostedService"/> (AB#8002). Bound from the
/// host's <c>StreamData:ComputedColumnBackfill</c> configuration section. The active tenant set is
/// taken from the shared <see cref="IRollupTenantSource"/>.
/// </summary>
public sealed class ComputedColumnBackfillOptions
{
    /// <summary>
    /// Interval between two drains. A drain looks at every stream-data tenant for queued
    /// computed-column backfills and runs them one after the other; a queued backfill therefore
    /// starts at most this long after it was queued. Default: 15 seconds.
    /// </summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Delay before the first drain, so the rest of the host (Mongo / Crate clients, CK cache)
    /// finishes initialising first. Default: 30 seconds.
    /// </summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(30);
}
