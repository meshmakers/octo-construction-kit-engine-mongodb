using System;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// Periodic background driver for the rollup orchestrator (rollup-archives concept §5). On each
/// tick it asks the configured <see cref="IRollupTenantSource"/> for the active tenant set, then
/// for every tenant it resolves the tenant-scoped <see cref="IRollupOrchestrator"/> via
/// <see cref="ISystemContext.TryFindTenantContextAsync"/> and drives a single
/// <see cref="IRollupOrchestrator.TickAsync"/>. Failures are isolated per-tenant — one bad tenant
/// does not stop the rest of the tick or the service itself.
/// </summary>
internal sealed class RollupOrchestratorHostedService : BackgroundService
{
    private readonly ISystemContext _systemContext;
    private readonly IRollupTenantSource _tenantSource;
    private readonly IOptionsMonitor<RollupOrchestratorOptions> _options;
    private readonly ILogger<RollupOrchestratorHostedService> _logger;

    public RollupOrchestratorHostedService(
        ISystemContext systemContext,
        IRollupTenantSource tenantSource,
        IOptionsMonitor<RollupOrchestratorOptions> options,
        ILogger<RollupOrchestratorHostedService> logger)
    {
        _systemContext = systemContext;
        _tenantSource = tenantSource;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Rollup orchestrator background service starting. Initial delay: {StartupDelay}, tick interval: {TickInterval}.",
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
                    // Outer catch so a top-level failure (e.g. tenant source throws) does not kill
                    // the service. Sleep the normal interval and retry.
                    _logger.LogError(ex, "Rollup orchestrator tick failed; will retry on next interval.");
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

        _logger.LogInformation("Rollup orchestrator background service stopped.");
    }

    private async Task TickAllTenantsAsync(CancellationToken cancellationToken)
    {
        var tenantIds = await _tenantSource.GetTenantIdsAsync(cancellationToken);
        if (tenantIds.Count == 0)
        {
            _logger.LogDebug("Rollup tick: no tenants from source; skipping.");
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
                    "Rollup tick for tenant '{TenantId}' failed; continuing with remaining tenants.", tenantId);
            }
        }
    }

    private async Task TickTenantAsync(string tenantId, CancellationToken cancellationToken)
    {
        var tenantContext = await _systemContext.TryFindTenantContextAsync(tenantId);
        if (tenantContext is null)
        {
            _logger.LogDebug(
                "Rollup tick: tenant '{TenantId}' not found via ISystemContext; skipping.", tenantId);
            return;
        }

        var orchestrator = tenantContext.GetRollupOrchestrator();
        if (orchestrator is null)
        {
            // Stream data disabled for this tenant, or no rollup store wired — quietly skip.
            return;
        }

        var committed = await orchestrator.TickAsync(cancellationToken);
        if (committed > 0)
        {
            _logger.LogInformation(
                "Rollup tick for tenant '{TenantId}' committed {Buckets} bucket(s).", tenantId, committed);
        }
    }
}
