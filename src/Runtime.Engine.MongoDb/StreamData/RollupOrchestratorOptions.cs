using System;
using System.Collections.Generic;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// Configuration for the <see cref="RollupOrchestratorHostedService"/> (rollup-archives concept §5).
/// Bound from the host's <c>StreamData:Rollup</c> configuration section via the standard
/// <c>IOptions</c> pattern.
/// </summary>
public sealed class RollupOrchestratorOptions
{
    /// <summary>
    /// Wall-clock interval between orchestrator ticks. Each tick fans out to every tenant
    /// returned by the configured <see cref="IRollupTenantSource"/>. Default: 30 seconds.
    /// </summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Delay applied once on startup before the first tick fires. Lets the rest of the host
    /// (Mongo / Crate clients, CK cache) finish initialising so the very first tick does not race
    /// with model imports. Default: 30 seconds.
    /// </summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When the default <see cref="ConfigBasedRollupTenantSource"/> is used, the orchestrator ticks
    /// exactly the tenants listed here. Empty list (default) ⇒ no ticks until a richer
    /// <see cref="IRollupTenantSource"/> is wired. Composition roots with dynamic tenant discovery
    /// register their own <see cref="IRollupTenantSource"/> and leave this empty.
    /// </summary>
    public IReadOnlyList<string> TenantIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Default ON (AB#4306): on every tick, additionally re-aggregate each activated rollup's
    /// CURRENT open bucket as a provisional row, without advancing the watermark. The forward loop
    /// alone only materialises a bucket once it has fully closed (bucketEnd &lt; now - lag), so a
    /// coarse rollup's "this month / this year so far" total would otherwise stay absent or frozen
    /// at the last backfill until the period ends — which is surprising for users. Keeping the open
    /// bucket fresh on the tick cadence is the expected behaviour, so it is on by default. Cheap on a
    /// cascaded ladder (each open bucket reads only a handful of rows from the finer level below).
    /// Set <c>StreamData:Rollup:RefreshOpenBucket=false</c> to disable it for a deployment where the
    /// extra per-tick write load on a very wide/deep rollup set is not wanted.
    /// </summary>
    public bool RefreshOpenBucket { get; set; } = true;
}
