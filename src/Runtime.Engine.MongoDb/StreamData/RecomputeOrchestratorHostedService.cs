using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// Periodic background driver for the recompute orchestrator (AB#4184). On each tick it asks the
/// shared <see cref="IRollupTenantSource"/> for the active tenant set, then for every tenant resolves
/// the tenant-scoped <c>IRecomputeOrchestrator</c> via
/// <see cref="Meshmakers.Octo.Runtime.Contracts.MongoDb.ISystemContext.TryFindTenantContextAsync"/>
/// and drives one tick. Failures are isolated per-tenant — one bad tenant does not stop the rest of
/// the tick or the service itself. Structurally identical to
/// <see cref="RollupOrchestratorHostedService"/>.
/// </summary>
internal sealed class RecomputeOrchestratorHostedService : BackgroundService
{
    private readonly ISystemContext _systemContext;
    private readonly IRollupTenantSource _tenantSource;
    private readonly IOptionsMonitor<RecomputeOrchestratorOptions> _options;
    private readonly ILogger<RecomputeOrchestratorHostedService> _logger;
    private readonly RestoreReconcileGate _gate;
    private readonly ConcurrentDictionary<string, bool> _validated = new(StringComparer.Ordinal);

    public RecomputeOrchestratorHostedService(
        ISystemContext systemContext,
        IRollupTenantSource tenantSource,
        IOptionsMonitor<RecomputeOrchestratorOptions> options,
        ILogger<RecomputeOrchestratorHostedService> logger)
    {
        _systemContext = systemContext;
        _tenantSource = tenantSource;
        _options = options;
        _logger = logger;
        _gate = new RestoreReconcileGate(logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Recompute orchestrator background service starting. Initial delay: {StartupDelay}, tick interval: {TickInterval}.",
            _options.CurrentValue.StartupDelay, _options.CurrentValue.TickInterval);

        try
        {
            await Task.Delay(_options.CurrentValue.StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAllTenantsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Recompute orchestrator tick failed; will retry on next interval.");
                }

                try
                {
                    await Task.Delay(_options.CurrentValue.TickInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown requested.
        }

        _logger.LogInformation("Recompute orchestrator background service stopped.");
    }

    private async Task TickAllTenantsAsync(CancellationToken cancellationToken)
    {
        var tenantIds = await _tenantSource.GetTenantIdsAsync(cancellationToken);
        if (tenantIds.Count == 0)
        {
            _logger.LogDebug("Recompute tick: no tenants from source; skipping.");
            return;
        }

        foreach (var tenantId in tenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await TickTenantAsync(tenantId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Recompute tick for tenant '{TenantId}' failed; continuing with remaining tenants.", tenantId);
            }
        }
    }

    private async Task TickTenantAsync(string tenantId, CancellationToken cancellationToken)
    {
        var tenantContext = await _systemContext.TryFindTenantContextAsync(tenantId);
        if (tenantContext is null)
        {
            _logger.LogDebug(
                "Recompute tick: tenant '{TenantId}' not found via ISystemContext; skipping.", tenantId);
            return;
        }

        var orchestrator = tenantContext.GetRecomputeOrchestrator();
        if (orchestrator is null)
        {
            // Stream data disabled for this tenant, or no rollup store wired — quietly skip.
            return;
        }

        // Once per process and tenant, read only: an inconsistent rollup is named in the log at start (AB#6968).
        await ValidateAtStartAsync(tenantId, tenantContext, cancellationToken);

        // Queued repairs run for a marked tenant too - they are what releases it (AB#6968).
        await RunQueuedRepairsAsync(tenantId, tenantContext, cancellationToken);

        // A tenant that was just restored is left alone until its rollups are reconciled: no recompute, no sweep.
        if (await _gate.IsBlockedAsync(tenantId, tenantContext, "the recompute tick and the sweep of superseded generations", cancellationToken))
        {
            return;
        }

        var recomputed = await orchestrator.TickAsync(cancellationToken);
        if (recomputed > 0)
        {
            _logger.LogInformation(
                "Recompute tick for tenant '{TenantId}' executed {Count} recompute(s).", tenantId, recomputed);
        }

        await SweepDeferredAsync(tenantId, tenantContext, cancellationToken);
    }

    private async Task RunQueuedRepairsAsync(string tenantId, ITenantContext tenantContext, CancellationToken cancellationToken)
    {
        var repository = tenantContext.GetStreamDataRepository();
        if (repository is null)
        {
            return;
        }

        try
        {
            var ran = await repository.RunPendingRollupRepairsAsync(cancellationToken);
            if (ran > 0)
            {
                _logger.LogInformation("Recompute tick for tenant '{TenantId}' ran {Count} queued rollup repair(s).", tenantId, ran);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Running the queued rollup repairs of tenant '{TenantId}' failed; the next tick tries again.", tenantId);
        }
    }

    /// <summary>
    /// Validates the rollups of a tenant the first time this process ticks it. The validator logs a warning per inconsistent rollup,
    /// naming rollup and range; this only adds the summary. A failure is logged and tried again with the next tick.
    /// </summary>
    private async Task ValidateAtStartAsync(string tenantId, ITenantContext tenantContext, CancellationToken cancellationToken)
    {
        if (!_validated.TryAdd(tenantId, true))
        {
            return;
        }

        var repository = tenantContext.GetStreamDataRepository();
        if (repository is null)
        {
            return;
        }

        try
        {
            var report = await repository.ValidateRollupsAsync(null, cancellationToken);
            if (!report.IsOk)
            {
                _logger.LogWarning(
                    "Tenant '{TenantId}' has {Count} inconsistent and {NotChecked} unchecked rollup(s) at start; validate them (details above) and repair them before relying on their values.",
                    tenantId, report.Rollups.Count(r => r.Findings.Count > 0), report.Rollups.Count(r => r.IsNotChecked));
            }
        }
        catch (ArchiveCopyNotSupportedException)
        {
            // A repository without the validator has nothing to report.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _validated.TryRemove(tenantId, out _);
            throw;
        }
        catch (Exception ex)
        {
            _validated.TryRemove(tenantId, out _);
            _logger.LogWarning(ex, "Validating the rollups of tenant '{TenantId}' at start failed; it is tried again with the next tick.", tenantId);
        }
    }

    /// <summary>
    /// Runs the sweeps of recomputed ranges whose grace period has passed (AB#6968). Every tick, so a rollup that is not
    /// recomputed again is still cleaned up, and so a sweep recorded before a restart is found. A failure is logged; the
    /// records stay and the next tick repeats.
    /// </summary>
    private async Task SweepDeferredAsync(string tenantId, ITenantContext tenantContext, CancellationToken cancellationToken)
    {
        var repository = tenantContext.GetStreamDataRepository();
        if (repository is null)
        {
            return;
        }

        try
        {
            var swept = await repository.SweepSupersededGenerationsAsync(cancellationToken);
            if (swept > 0)
            {
                _logger.LogInformation(
                    "Recompute tick for tenant '{TenantId}' ran {Count} deferred sweep(s) of superseded generations.", tenantId, swept);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Deferred sweep of superseded generations for tenant '{TenantId}' failed; the records stay and the next tick repeats.", tenantId);
        }
    }
}
