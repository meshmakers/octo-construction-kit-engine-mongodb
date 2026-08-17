using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.DisplayRules;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.DisplayRules;

/// <summary>
///     Periodic background driver for the display-rule backfill sweep (AB#4812). On each tick it
///     drains the due tasks from <see cref="IDisplayRuleSweepStore" /> (lease-protected, so
///     multiple service instances cooperate), resolves the tenant context per task and executes the
///     sweep. Failures are recorded per task with a bounded retry budget — one bad task does not
///     stop the tick or the service. Structurally modeled on <c>RecomputeOrchestratorHostedService</c>.
/// </summary>
internal sealed class DisplayRuleSweepHostedService : BackgroundService
{
    private readonly ISystemContext _systemContext;
    private readonly IDisplayRuleSweepStore _sweepStore;
    private readonly ICkCacheService _ckCacheService;
    private readonly IOptionsMonitor<DisplayRuleSweepOptions> _options;
    private readonly ILogger<DisplayRuleSweepHostedService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public DisplayRuleSweepHostedService(
        ISystemContext systemContext,
        IDisplayRuleSweepStore sweepStore,
        ICkCacheService ckCacheService,
        IOptionsMonitor<DisplayRuleSweepOptions> options,
        ILogger<DisplayRuleSweepHostedService> logger,
        ILoggerFactory loggerFactory)
    {
        _systemContext = systemContext;
        _sweepStore = sweepStore;
        _ckCacheService = ckCacheService;
        _options = options;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Display rule sweep background service starting. Initial delay: {StartupDelay}, tick interval: {TickInterval}.",
            _options.CurrentValue.StartupDelay, _options.CurrentValue.TickInterval);

        try
        {
            await Task.Delay(_options.CurrentValue.StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await DrainDueTasksAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Display rule sweep tick failed; will retry on next interval.");
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

        _logger.LogInformation("Display rule sweep background service stopped.");
    }

    private async Task DrainDueTasksAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var sweeper = new DisplayRuleSweeper(_ckCacheService, _sweepStore,
            _loggerFactory.CreateLogger<DisplayRuleSweeper>());

        while (!cancellationToken.IsCancellationRequested)
        {
            var sweepRecord = await _sweepStore.TryClaimAsync(_leaseOwner, options.LeaseDuration,
                options.MinRetryInterval, options.MaxAttempts, cancellationToken);
            if (sweepRecord == null)
            {
                return;
            }

            try
            {
                var tenantContext = await _systemContext.TryFindTenantContextAsync(sweepRecord.TenantId);
                if (tenantContext == null)
                {
                    _logger.LogWarning(
                        "Display rule sweep: tenant '{TenantId}' not found; completing task for type '{CkTypeId}'.",
                        sweepRecord.TenantId, sweepRecord.CkTypeId);
                    await _sweepStore.CompleteAsync(sweepRecord.TenantId, sweepRecord.CkTypeId, cancellationToken);
                    continue;
                }

                var updatedCount = await sweeper.SweepAsync(tenantContext, sweepRecord, options.PageSize,
                    cancellationToken);

                await _sweepStore.CompleteAsync(sweepRecord.TenantId, sweepRecord.CkTypeId, cancellationToken);
                _logger.LogInformation(
                    "Display rule sweep for type '{CkTypeId}' in tenant '{TenantId}' completed; {Count} entities updated.",
                    sweepRecord.CkTypeId, sweepRecord.TenantId, updatedCount);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Display rule sweep for type '{CkTypeId}' in tenant '{TenantId}' failed (attempt {Attempt}).",
                    sweepRecord.CkTypeId, sweepRecord.TenantId, sweepRecord.AttemptCount + 1);
                await _sweepStore.RecordFailureAsync(sweepRecord.TenantId, sweepRecord.CkTypeId, ex.Message,
                    CancellationToken.None);
            }
        }
    }
}
