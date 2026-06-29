using System;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// Configuration for the <see cref="RecomputeOrchestratorHostedService"/> (AB#4184). Bound from the
/// host's <c>StreamData:Recompute</c> configuration section via the standard <c>IOptions</c>
/// pattern. The active tenant set is taken from the shared <see cref="IRollupTenantSource"/>.
/// </summary>
public sealed class RecomputeOrchestratorOptions
{
    /// <summary>
    /// Wall-clock interval between recompute ticks. Each tick fans out to every stream-data tenant
    /// and drains pending recompute ranges. Recompute is heavier than the forward rollup tick, so
    /// the default is coarser. Default: 60 seconds.
    /// </summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Delay applied once on startup before the first tick fires, so the rest of the host (Mongo /
    /// Crate clients, CK cache) finishes initialising first. Default: 60 seconds.
    /// </summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(60);
}
