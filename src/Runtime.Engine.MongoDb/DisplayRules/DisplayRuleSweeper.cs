using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DisplayRules;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.DisplayRules;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.DisplayRules;

/// <summary>
///     Executes one display-rule backfill sweep task (AB#4812): pages through all entities of the
///     type's subtree (the polymorphic type query includes every descendant), re-evaluates each
///     entity's effective display rules and writes only the changed fields as partial updates
///     (empty string = clear sentinel, see the Mongo update mapper). Idempotent — unchanged
///     entities cause no write, so a retried sweep only redoes the remainder.
/// </summary>
internal sealed class DisplayRuleSweeper(
    ICkCacheService ckCacheService,
    IDisplayRuleSweepStore sweepStore,
    ILogger<DisplayRuleSweeper> logger)
{
    /// <summary>
    ///     Sweeps one task. Returns the number of entities whose display fields were rewritten.
    /// </summary>
    internal async Task<long> SweepAsync(ITenantContext tenantContext, DisplayRuleSweepRecord sweepRecord,
        int pageSize, CancellationToken cancellationToken)
    {
        var tenantId = sweepRecord.TenantId;
        var tenantRepository = tenantContext.GetTenantRepositoryAsAdmin();
        if (tenantRepository is not TenantRepository mongoTenantRepository)
        {
            throw new InvalidOperationException(
                $"Display rule sweep requires a Mongo tenant repository, got '{tenantRepository.GetType().Name}'.");
        }

        if (!ckCacheService.IsTenantLoaded(tenantId))
        {
            await mongoTenantRepository.LoadCacheForTenantAsync(ckCacheService).ConfigureAwait(false);
        }

        var rootCkTypeId = new CkId<CkTypeId>(sweepRecord.CkTypeId);
        if (!ckCacheService.TryGetCkType(tenantId, rootCkTypeId, out var rootTypeGraph) || rootTypeGraph == null)
        {
            logger.LogInformation(
                "Display rule sweep: type '{CkTypeId}' no longer exists in tenant '{TenantId}'; completing task.",
                sweepRecord.CkTypeId, tenantId);
            return 0;
        }

        // Types above the collection roots (e.g. System/Entity) have no collection of their own;
        // their entities live in many collections. Fan the task out to the collection roots below
        // this type — they partition all concrete entities of the subtree — and complete this task.
        if (rootTypeGraph.DefiningCollectionRootCkTypeId == null)
        {
            var fannedOut = 0;
            foreach (var derivedTypeId in rootTypeGraph.GetAllDerivedTypes(false))
            {
                if (ckCacheService.TryGetCkType(tenantId, derivedTypeId, out var derivedGraph) &&
                    derivedGraph is { IsCollectionRoot: true })
                {
                    await sweepStore.EnqueueAsync(tenantId, derivedTypeId.FullName, cancellationToken)
                        .ConfigureAwait(false);
                    fannedOut++;
                }
            }

            logger.LogInformation(
                "Display rule sweep: type '{CkTypeId}' in tenant '{TenantId}' has no collection root; fanned out to {Count} collection root(s).",
                sweepRecord.CkTypeId, tenantId, fannedOut);
            return 0;
        }

        var session = await mongoTenantRepository.GetSessionAsync().ConfigureAwait(false);
        try
        {
            return await SweepEntitiesAsync(mongoTenantRepository, session, tenantId, rootTypeGraph, pageSize,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
        }
    }

    private async Task<long> SweepEntitiesAsync(TenantRepository tenantRepository, IOctoSession session,
        string tenantId, ConstructionKit.Contracts.DependencyGraph.CkTypeGraph rootTypeGraph, int pageSize,
        CancellationToken cancellationToken)
    {
        var rtCkTypeId = rootTypeGraph.CkTypeId.ToRtCkId();
        var updatedCount = 0L;
        var skip = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // "_id" (= rtId) is the stable paging cursor; the sweep only writes display fields and
            // rtVersion, so the sort key and filter membership never change while paging.
            var queryOptions = RtEntityQueryOptions.Create()
                .SortOrder(Constants.IdField, SortOrders.Ascending);
            var page = await tenantRepository
                .GetRtEntitiesByTypeAsync(session, rtCkTypeId, queryOptions, skip, pageSize)
                .ConfigureAwait(false);
            var pageItems = page.Items.ToList();

            var partialUpdates = new List<RtEntity>();
            foreach (var rtEntity in pageItems)
            {
                var partialUpdate = CreatePartialUpdate(ckCacheService, tenantId, rtEntity);
                if (partialUpdate != null)
                {
                    partialUpdates.Add(partialUpdate);
                }
            }

            if (partialUpdates.Count > 0)
            {
                var collection = tenantRepository.DataSource.GetRtCollection<RtEntity>(rootTypeGraph);
                await collection.UpdateOneAsync(session, partialUpdates).ConfigureAwait(false);
                updatedCount += partialUpdates.Count;
            }

            if (pageItems.Count < pageSize)
            {
                return updatedCount;
            }

            skip += pageSize;
        }
    }

    /// <summary>
    ///     Computes the target display fields for an entity from its own type's effective rules and
    ///     returns a partial update document carrying only the changed fields — or null when the
    ///     stored values already match. Field semantics on the partial document: null = leave
    ///     unchanged, empty string = clear.
    /// </summary>
    internal static RtEntity? CreatePartialUpdate(ICkCacheService ckCacheService, string tenantId, RtEntity rtEntity)
    {
        // Each entity may be a derived type overriding the rules — evaluate with its own type graph.
        var entityTypeGraph = ckCacheService.GetRtCkType(tenantId, rtEntity.GetRtCkTypeId());

        var targetDisplayName = RtDisplayRuleEvaluator.ComputeValue(entityTypeGraph.DisplayNameRule, rtEntity);
        var targetDisplayDescription =
            RtDisplayRuleEvaluator.ComputeValue(entityTypeGraph.DisplayDescriptionRule, rtEntity);

        var displayNameChanged = !string.Equals(targetDisplayName, rtEntity.RtDisplayName, StringComparison.Ordinal);
        var displayDescriptionChanged =
            !string.Equals(targetDisplayDescription, rtEntity.RtDisplayDescription, StringComparison.Ordinal);
        if (!displayNameChanged && !displayDescriptionChanged)
        {
            return null;
        }

        return new RtEntity(rtEntity.GetRtCkTypeId(), rtEntity.RtId)
        {
            RtDisplayName = displayNameChanged ? targetDisplayName ?? string.Empty : null,
            RtDisplayDescription = displayDescriptionChanged ? targetDisplayDescription ?? string.Empty : null
        };
    }
}
