using System;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
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

        var recomputed = await orchestrator.TickAsync(cancellationToken);
        if (recomputed > 0)
        {
            _logger.LogInformation(
                "Recompute tick for tenant '{TenantId}' executed {Count} recompute(s).", tenantId, recomputed);
        }
    }
}
