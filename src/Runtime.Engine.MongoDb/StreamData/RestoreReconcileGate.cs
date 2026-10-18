using System.Collections.Concurrent;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;

/// <summary>
/// The gate of the rollup and recompute ticks on the <b>restore reconcile pending</b> marker (AB#5967 slice 2 / AB#6968): a tenant that
/// was restored with archive data stays untouched - no forward aggregation, no recompute, no reclaim, no sweep - until its rollups
/// validate ok or a repair has run, because a tick on an unreconciled rollup would aggregate from the wrong watermark, flip pointers
/// onto missing rows or delete rows a pointer still names. A tenant without the marker (every tenant and backup from before the
/// feature) is not held back and costs one small statement.
/// </summary>
/// <remarks>
/// The reason is logged at information level when a tenant becomes blocked and again at debug level on every tick, so a blocked tenant
/// is explained once and does not fill the log. A repository without the marker (<see cref="ArchiveCopyNotSupportedException"/>) never blocks.
/// </remarks>
internal sealed class RestoreReconcileGate(ILogger logger)
{
    private readonly ConcurrentDictionary<string, bool> _reported = new(StringComparer.Ordinal);

    /// <summary>True when the tenant is marked pending: the caller skips its tick.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="tenantContext">Its context.</param>
    /// <param name="skipped">What is not done, for the log: e.g. "the rollup tick".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> IsBlockedAsync(
        string tenantId, ITenantContext tenantContext, string skipped, CancellationToken cancellationToken)
    {
        var repository = tenantContext.GetStreamDataRepository();
        if (repository is null)
        {
            return false;
        }

        bool pending;
        try
        {
            pending = await repository.IsRestoreReconcilePendingAsync(cancellationToken);
        }
        catch (ArchiveCopyNotSupportedException)
        {
            return false;
        }

        if (!pending)
        {
            _reported.TryRemove(tenantId, out _);
            return false;
        }

        if (_reported.TryAdd(tenantId, true))
        {
            logger.LogInformation(
                "Tenant '{TenantId}': {Skipped} is skipped because the tenant is marked restore reconcile pending - a restore with archive data " +
                "finished and its rollups were neither validated ok nor repaired yet. Validate or repair the rollups to release it.",
                tenantId, skipped);
        }
        else
        {
            logger.LogDebug("Tenant '{TenantId}': {Skipped} is skipped (restore reconcile pending).", tenantId, skipped);
        }

        return true;
    }
}
