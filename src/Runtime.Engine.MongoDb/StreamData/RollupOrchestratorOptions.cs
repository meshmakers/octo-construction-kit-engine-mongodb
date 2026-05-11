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
}
