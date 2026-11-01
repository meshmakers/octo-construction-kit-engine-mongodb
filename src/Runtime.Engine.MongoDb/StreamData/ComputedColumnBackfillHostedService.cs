using System;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// Background driver of the computed-column backfill jobs (AB#8002). Adding a computed column or
/// changing its formula only queues its backfill; this service runs the queue: on every tick it asks
/// the shared <see cref="IRollupTenantSource"/> for the tenants and drains each tenant's queued
/// backfills through <c>IArchiveLifecycleService.RunPendingComputedColumnBackfillsAsync</c>, under the
/// host's stopping token — never a request's — so a client that times out or disconnects cannot
/// cancel a backfill. Tenants are drained one after the other and every tenant runs its backfills
/// sequentially, so at most one backfill per process loads CrateDB at a time. Failures are isolated
/// per tenant. Structurally like <see cref="RecomputeOrchestratorHostedService"/>, but a service of
/// its own so a long backfill never delays the rollup recompute tick.
/// </summary>
internal sealed class ComputedColumnBackfillHostedService : BackgroundService
{
    private readonly ISystemContext _systemContext;
    private readonly IRollupTenantSource _tenantSource;
    private readonly IOptionsMonitor<ComputedColumnBackfillOptions> _options;
    private readonly ILogger<ComputedColumnBackfillHostedService> _logger;

    public ComputedColumnBackfillHostedService(
        ISystemContext systemContext,
        IRollupTenantSource tenantSource,
        IOptionsMonitor<ComputedColumnBackfillOptions> options,
        ILogger<ComputedColumnBackfillHostedService> logger)
    {
        _systemContext = systemContext;
        _tenantSource = tenantSource;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Computed-column backfill service starting. Initial delay: {StartupDelay}, tick interval: {TickInterval}.",
            _options.CurrentValue.StartupDelay, _options.CurrentValue.TickInterval);

        try
        {
            await Task.Delay(_options.CurrentValue.StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await DrainAllTenantsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Computed-column backfill drain failed; will retry on next interval.");
                }

                await Task.Delay(_options.CurrentValue.TickInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown requested; a running backfill handed its job back as Pending.
        }

        _logger.LogInformation("Computed-column backfill service stopped.");
    }

    /// <summary>One drain over every tenant. Internal for tests.</summary>
    internal async Task DrainAllTenantsAsync(CancellationToken cancellationToken)
    {
        var tenantIds = await _tenantSource.GetTenantIdsAsync(cancellationToken);
        foreach (var tenantId in tenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DrainTenantAsync(tenantId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Computed-column backfill drain for tenant '{TenantId}' failed; continuing with remaining tenants.",
                    tenantId);
            }
        }
    }

    private async Task DrainTenantAsync(string tenantId, CancellationToken cancellationToken)
    {
        var tenantContext = await _systemContext.TryFindTenantContextAsync(tenantId);
        var lifecycle = tenantContext?.GetArchiveLifecycleService();
        if (lifecycle is null)
        {
            // Unknown tenant or stream data disabled — nothing to drain.
            return;
        }

        var completed = await lifecycle.RunPendingComputedColumnBackfillsAsync(cancellationToken);
        if (completed > 0)
        {
            _logger.LogInformation(
                "Computed-column backfill drain for tenant '{TenantId}' completed {Count} backfill(s).",
                tenantId, completed);
        }
    }
}
