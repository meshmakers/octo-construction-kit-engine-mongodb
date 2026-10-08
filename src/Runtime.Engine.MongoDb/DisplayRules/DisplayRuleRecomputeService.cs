using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.DisplayRules;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.DisplayRules;

/// <summary>
///     Default <see cref="IDisplayRuleRecomputeService" /> (AB#5945). Resolves the tenant's CK cache
///     and enqueues one sweep task per type into <see cref="IDisplayRuleSweepStore" />; the actual
///     recompute runs in the display-rule sweep background service (asset repository).
/// </summary>
internal sealed class DisplayRuleRecomputeService(
    ISystemContext systemContext,
    ICkCacheService ckCacheService,
    IDisplayRuleSweepStore sweepStore,
    ILogger<DisplayRuleRecomputeService> logger) : IDisplayRuleRecomputeService
{
    public async Task<IReadOnlyList<string>> EnqueueRecomputeAsync(string tenantId, string? ckTypeId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var tenantContext = await systemContext.FindTenantContextAsync(tenantId).ConfigureAwait(false);
        var normalizedTenantId = tenantContext.TenantId;
        if (!ckCacheService.IsTenantLoaded(normalizedTenantId))
        {
            await tenantContext.LoadCacheForTenantAsync().ConfigureAwait(false);
        }

        var sweepKeys = SelectSweepKeys(ckCacheService, normalizedTenantId, ckTypeId);
        foreach (var sweepKey in sweepKeys)
        {
            await sweepStore.EnqueueAsync(normalizedTenantId, sweepKey, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Display rule recompute requested for tenant '{TenantId}' (type filter: '{CkTypeId}'); {Count} sweep task(s) enqueued: {SweepKeys}",
            normalizedTenantId, ckTypeId ?? "<all>", sweepKeys.Count, string.Join(", ", sweepKeys));

        return sweepKeys;
    }

    /// <summary>
    ///     Selects the sweep keys: the given type (which must carry an effective display rule), or every
    ///     type declaring a rule itself - a declaring type's sweep pages its subtree polymorphically, so
    ///     inheriting types need no task of their own.
    /// </summary>
    internal static IReadOnlyList<string> SelectSweepKeys(ICkCacheService ckCacheService, string tenantId,
        string? ckTypeId)
    {
        if (!string.IsNullOrWhiteSpace(ckTypeId))
        {
            if (!ckCacheService.TryGetRtCkType(tenantId, new RtCkId<CkTypeId>(ckTypeId), out var typeGraph) ||
                typeGraph == null)
            {
                throw new ArgumentException($"CK type '{ckTypeId}' does not exist in tenant '{tenantId}'.",
                    nameof(ckTypeId));
            }

            if (string.IsNullOrWhiteSpace(typeGraph.DisplayNameRule) &&
                string.IsNullOrWhiteSpace(typeGraph.DisplayDescriptionRule))
            {
                throw new ArgumentException(
                    $"CK type '{ckTypeId}' has no display rule in tenant '{tenantId}'; nothing to recompute.",
                    nameof(ckTypeId));
            }

            return [typeGraph.CkTypeId.FullName];
        }

        return ckCacheService.GetCkTypes(tenantId)
            .Where(t => t.DisplayNameRuleDeclared || t.DisplayDescriptionRuleDeclared)
            .Select(t => t.CkTypeId.FullName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }
}
