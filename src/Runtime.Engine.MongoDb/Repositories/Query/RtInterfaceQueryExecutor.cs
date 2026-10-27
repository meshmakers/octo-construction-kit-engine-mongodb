using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

/// <summary>
///     Executes the query of all implementers of a CK interface (AB#7019, decision of spike AB#6930, option A):
///     fan-out over the collections of the implementing types, one sorted top-(skip+take) query per collection and an
///     application-side k-way merge. See <see cref="ITenantRepository.GetRtEntitiesByInterfaceAsync" /> for the
///     contract and <c>CLAUDE.md</c> ("Interface query") for the design.
/// </summary>
internal sealed class RtInterfaceQueryExecutor(
    IMetricsContext metricsContext,
    ICkCacheService ckCacheService,
    string tenantId,
    IMongoDbRepositoryDataSource dataSource)
{
    /// <summary>Upper bound of concurrently running collection queries when the session has no transaction.</summary>
    private const int MaxParallelQueries = 8;

    private static readonly string[] SystemFieldNames =
    [
        nameof(RtEntity.RtId), nameof(RtEntity.CkTypeId), nameof(RtEntity.RtCreationDateTime),
        nameof(RtEntity.RtChangedDateTime), nameof(RtEntity.RtVersion), nameof(RtEntity.RtWellKnownName),
        nameof(RtEntity.RtCreatedBy)
    ];

    /// <summary>
    ///     Checks everything that can be refused before a collection is touched. Order: page limit, text search,
    ///     aggregation, navigation / geospatial, members of the interface.
    /// </summary>
    internal static void Validate(RtCkId<CkInterfaceId> ckInterfaceId, CkInterfaceGraph interfaceGraph,
        RtEntityQueryOptions queryOptions, int skip, int take)
    {
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "skip must not be negative.");
        }

        if (take < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "take must not be negative.");
        }

        if ((long)skip + take > RtInterfaceQueryLimits.MaxSkipPlusTake)
        {
            throw InterfaceQueryException.PageTooLarge(skip, take, RtInterfaceQueryLimits.MaxSkipPlusTake);
        }

        if (queryOptions.TextSearchFilter != null || queryOptions.AttributeSearchFilter != null)
        {
            throw InterfaceQueryException.TextSearchNotSupported(ckInterfaceId);
        }

        if (queryOptions.FieldAggregation != null || queryOptions.ResultAggregation != null)
        {
            throw InterfaceQueryException.AggregationNotSupported(ckInterfaceId);
        }

        if (queryOptions.GeospatialFilters is { Count: > 0 })
        {
            throw InterfaceQueryException.NavigationNotSupported(ckInterfaceId, string.Empty);
        }

        var members = interfaceGraph.AllAttributes.Values.Select(a => a.AttributeName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateFilterPaths(queryOptions))
        {
            EnsureMember(ckInterfaceId, members, path, "filter");
        }

        foreach (var sortOrder in queryOptions.SortOrders ?? [])
        {
            EnsureMember(ckInterfaceId, members, sortOrder.AttributePath, "sort");
        }
    }

    private static IEnumerable<string> EnumerateFilterPaths(FieldFilterCriteria criteria)
    {
        foreach (var filter in criteria.FieldFilters ?? [])
        {
            yield return filter.AttributePath;
        }

        foreach (var nested in criteria.NestedFilters ?? [])
        {
            foreach (var path in EnumerateFilterPaths(nested))
            {
                yield return path;
            }
        }
    }

    private static void EnsureMember(RtCkId<CkInterfaceId> ckInterfaceId, HashSet<string> members, string path,
        string usage)
    {
        if (path.Contains("->", StringComparison.Ordinal) || path.Contains("::", StringComparison.Ordinal))
        {
            throw InterfaceQueryException.NavigationNotSupported(ckInterfaceId, path);
        }

        var firstSegment = path.Split('.')[0];
        if (members.Contains(firstSegment) ||
            SystemFieldNames.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        throw InterfaceQueryException.UnknownMember(ckInterfaceId, path, usage);
    }

    /// <summary>
    ///     Resolves the collections of the implementing types. Every plan queries exactly one collection with the
    ///     list of implementing types that live in it. Types the caller can never see (protected and neither
    ///     allowed nor owned-only) are left out of the list; a collection with nothing left is not queried.
    /// </summary>
    internal List<CollectionPlan> BuildPlans(RtCkId<CkInterfaceId> ckInterfaceId, CkInterfaceGraph interfaceGraph,
        RtEntityQueryOptions queryOptions, RtDataSecurityQueryFilter? securityFilter)
    {
        var graphs = interfaceGraph.ImplementingTypes
            .Select(id => ckCacheService.GetCkType(tenantId, id))
            .Where(g => !g.IsAbstract && g.DefiningCollectionRootCkTypeId != null)
            .ToList();

        var groups = graphs
            .GroupBy(g => g.DefiningCollectionRootCkTypeId!.ToString(), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        if (groups.Count > RtInterfaceQueryLimits.MaxCollectionRoots)
        {
            throw InterfaceQueryException.TooManyCollectionRoots(ckInterfaceId, groups.Count,
                RtInterfaceQueryLimits.MaxCollectionRoots);
        }

        var includeArchived = queryOptions.GlobalFilter?.IncludeArchived ?? false;
        var plans = new List<CollectionPlan>();
        foreach (var group in groups)
        {
            var typeGraphs = group.OrderBy(g => g.CkTypeId.ToString(), StringComparer.Ordinal).ToList();
            var visible = typeGraphs
                .Select(g => g.CkTypeId.ToRtCkId())
                .Where(id => !IsAlwaysDenied(securityFilter, id))
                .ToList();
            if (visible.Count == 0)
            {
                continue;
            }

            // The first visible implementer binds the attribute paths: every implementer has every member of the
            // interface (compiler rule), and the stored path of an attribute does not depend on the type.
            var representative = typeGraphs.First(g => visible.Contains(g.CkTypeId.ToRtCkId()));
            var query = new SingleOriginRtQuery<RtEntity>(metricsContext, ckCacheService, tenantId, representative,
                dataSource, queryOptions.Language, includeArchived);
            query.RestrictToCkTypeIds(visible);
            query.AddSecurityFilter(securityFilter);
            query.AddFieldFilterCriteria(queryOptions);
            query.AddPostStagesToPipeline(queryOptions.SortOrders);
            plans.Add(new CollectionPlan(query, group.Key, visible));
        }

        return plans;
    }

    private static bool IsAlwaysDenied(RtDataSecurityQueryFilter? filter, RtCkId<CkTypeId> ckTypeId)
    {
        if (filter is not { HasEnforcement: true })
        {
            return false;
        }

        var name = ckTypeId.SemanticVersionedFullName;
        return filter.ProtectedCkTypeIds.Contains(name) &&
               !filter.AllowedCkTypeIds.Contains(name) &&
               !filter.OwnedOnlyCkTypeIds.Contains(name);
    }

    /// <summary>
    ///     Runs the plans and returns the requested page.
    /// </summary>
    internal async Task<RtInterfaceQueryResult> ExecuteAsync(IOctoSession session, List<CollectionPlan> plans,
        int skip, int take, bool includeTotalCount)
    {
        var sessionInternal = session as IOctoSessionInternal;
        // MongoDB runs one operation at a time inside a transaction: the collections are then queried one after the
        // other. Without a transaction they run in parallel on implicit sessions.
        var inTransaction = sessionInternal?.IsTransactionActive == true;
        var handle = inTransaction ? sessionInternal!.SessionHandle : null;

        Task<long?> countTask = includeTotalCount
            ? CountAsync(plans, handle, parallel: !inTransaction)
            : Task.FromResult<long?>(null);
        // A started count must finish before the data queries of a transaction session start.
        long? total = null;
        if (inTransaction)
        {
            total = await countTask.ConfigureAwait(false);
        }

        var items = new List<RtEntity>();
        if (take > 0 && plans.Count > 0)
        {
            var fetchLimit = skip + take + 1;
            var perCollection = await RunAsync(plans, plan => FetchAsync(plan, handle, skip + take, fetchLimit),
                parallel: !inTransaction).ConfigureAwait(false);
            var window = Merge(perCollection, plans[0].Query.SortKeys, skip, take);
            items = Materialize(window);
        }

        if (!inTransaction)
        {
            total = await countTask.ConfigureAwait(false);
        }

        return new RtInterfaceQueryResult(items, total);
    }

    private async Task<long?> CountAsync(List<CollectionPlan> plans, IClientSessionHandle? handle, bool parallel)
    {
        var counts = await RunAsync(plans, async plan =>
        {
            var filter = plan.Query.BuildMatchFilter() ?? Builders<RtEntity>.Filter.Empty;
            var collection = plan.Query.GetMongoCollection();
            return handle == null
                ? await collection.CountDocumentsAsync(filter).ConfigureAwait(false)
                : await collection.CountDocumentsAsync(handle, filter).ConfigureAwait(false);
        }, parallel).ConfigureAwait(false);
        return counts.Sum();
    }

    private static async Task<List<TResult>> RunAsync<TResult>(List<CollectionPlan> plans,
        Func<CollectionPlan, Task<TResult>> work, bool parallel)
    {
        if (!parallel || plans.Count == 1)
        {
            var results = new List<TResult>(plans.Count);
            foreach (var plan in plans)
            {
                results.Add(await work(plan).ConfigureAwait(false));
            }

            return results;
        }

        using var gate = new SemaphoreSlim(MaxParallelQueries);
        var tasks = plans.Select(async plan =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await work(plan).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
    }

    /// <summary>
    ///     Fetches the first <paramref name="windowEnd" /> rows of one collection in the global order. The
    ///     collection is asked for one row more: when that row ties with the last wanted one on every sort key, the
    ///     rows that tie are fetched completely ordered by rtId, because the server order within a tie is arbitrary
    ///     and the global tie-break is the rtId.
    /// </summary>
    private async Task<List<InterfaceRow>> FetchAsync(CollectionPlan plan, IClientSessionHandle? handle,
        int windowEnd, int fetchLimit)
    {
        var query = plan.Query;
        var sort = query.CombinedSortDefinition ?? Builders<RtEntity>.Sort.Ascending(Constants.IdField);
        var stages = new List<IPipelineStageDefinition>();
        var filter = query.BuildMatchFilter();
        if (filter != null)
        {
            stages.Add(PipelineStageDefinitionBuilder.Match(filter));
        }

        stages.Add(PipelineStageDefinitionBuilder.Sort(sort));
        stages.Add(LimitStage(fetchLimit));

        var rows = ToRows(await AggregateAsync(query, handle, stages).ConfigureAwait(false), query.SortKeys);
        if (rows.Count == fetchLimit && query.SortKeys.Count > 0 && KeysEqual(rows[windowEnd - 1], rows[windowEnd]))
        {
            var tiedKeys = rows[windowEnd].Keys;
            var tieFilters = new List<FilterDefinition<RtEntity>>();
            if (filter != null)
            {
                tieFilters.Add(filter);
            }

            for (var i = 0; i < tiedKeys.Length; i++)
            {
                tieFilters.Add(new BsonDocumentFilterDefinition<RtEntity>(
                    new BsonDocument(query.SortKeys[i].Path, tiedKeys[i])));
            }

            var tieStages = new List<IPipelineStageDefinition>
            {
                PipelineStageDefinitionBuilder.Match(Builders<RtEntity>.Filter.And(tieFilters)),
                PipelineStageDefinitionBuilder.Sort(Builders<RtEntity>.Sort.Ascending(Constants.IdField)),
                LimitStage(windowEnd)
            };
            var tied = ToRows(await AggregateAsync(query, handle, tieStages).ConfigureAwait(false), query.SortKeys);
            var kept = rows.Where(r => !KeysEqual(r, rows[windowEnd])).ToList();
            kept.AddRange(tied);
            rows = kept;
        }

        var comparer = new RowComparer(query.SortKeys);
        rows.Sort(comparer);
        return rows.Count > windowEnd ? rows.GetRange(0, windowEnd) : rows;
    }

    /// <summary>The closing $limit, declared with the raw document as output: the pipeline hands the documents on unmapped.</summary>
    private static IPipelineStageDefinition LimitStage(int limit) =>
        new BsonDocumentPipelineStageDefinition<RtEntity, BsonDocument>(new BsonDocument("$limit", limit));

    private static async Task<List<BsonDocument>> AggregateAsync(SingleOriginRtQuery<RtEntity> query,
        IClientSessionHandle? handle, List<IPipelineStageDefinition> stages)
    {
        var pipeline = PipelineDefinition<RtEntity, BsonDocument>.Create(stages);
        var collection = query.GetMongoCollection();
        using var cursor = handle == null
            ? await collection.AggregateAsync(pipeline).ConfigureAwait(false)
            : await collection.AggregateAsync(handle, pipeline).ConfigureAwait(false);
        return await cursor.ToListAsync().ConfigureAwait(false);
    }

    private static List<InterfaceRow> ToRows(List<BsonDocument> documents,
        IReadOnlyList<(string Path, bool Descending)> sortKeys)
    {
        var rows = new List<InterfaceRow>(documents.Count);
        foreach (var document in documents)
        {
            var keys = new BsonValue[sortKeys.Count];
            for (var i = 0; i < keys.Length; i++)
            {
                keys[i] = ExtractKey(document, sortKeys[i].Path, sortKeys[i].Descending);
            }

            rows.Add(new InterfaceRow(document, keys, document[Constants.IdField]));
        }

        return rows;
    }

    private static bool KeysEqual(InterfaceRow a, InterfaceRow b)
    {
        for (var i = 0; i < a.Keys.Length; i++)
        {
            if (CompareValues(a.Keys[i], b.Keys[i]) != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     K-way merge of the per-collection lists (each sorted by <see cref="RowComparer" />): the rows
    ///     [skip, skip + take) of the global order.
    /// </summary>
    private static List<InterfaceRow> Merge(List<List<InterfaceRow>> lists,
        IReadOnlyList<(string Path, bool Descending)> sortKeys, int skip, int take)
    {
        var comparer = new RowComparer(sortKeys);
        var queue = new PriorityQueue<(int List, int Index), InterfaceRow>(comparer);
        for (var i = 0; i < lists.Count; i++)
        {
            if (lists[i].Count > 0)
            {
                queue.Enqueue((i, 0), lists[i][0]);
            }
        }

        var window = new List<InterfaceRow>(take);
        var position = 0;
        while (queue.Count > 0 && window.Count < take)
        {
            var (list, index) = queue.Dequeue();
            if (position >= skip)
            {
                window.Add(lists[list][index]);
            }

            position++;
            if (index + 1 < lists[list].Count)
            {
                queue.Enqueue((list, index + 1), lists[list][index + 1]);
            }
        }

        return window;
    }

    private List<RtEntity> Materialize(List<InterfaceRow> window)
    {
        var entities = window.Select(r => BsonSerializer.Deserialize<RtEntity>(r.Document)).ToList();
        // AB#5533: legacy strings in Secret slots are handed out as RtSecretValue.LegacyPlaintext, as in every
        // other query.
        new SecretAttributeReadNormalizer(ckCacheService, tenantId).Normalize(entities);
        return entities;
    }

    // ---- sort keys ----------------------------------------------------------------------------------------------

    /// <summary>
    ///     The value MongoDB sorts a document by: the value at the path; a missing value counts as null; of an array
    ///     (also along the path) the smallest element for an ascending and the largest for a descending sort.
    /// </summary>
    internal static BsonValue ExtractKey(BsonDocument document, string path, bool descending)
    {
        var values = new List<BsonValue>();
        Collect(document, path.Split('.'), 0, values);
        if (values.Count == 0)
        {
            return BsonNull.Value;
        }

        var best = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            var c = CompareValues(values[i], best);
            if (descending ? c > 0 : c < 0)
            {
                best = values[i];
            }
        }

        return best;
    }

    private static void Collect(BsonValue node, string[] segments, int index, List<BsonValue> values)
    {
        if (index == segments.Length)
        {
            if (node is BsonArray array)
            {
                if (array.Count == 0)
                {
                    values.Add(BsonNull.Value);
                }

                foreach (var element in array)
                {
                    values.Add(element);
                }
            }
            else
            {
                values.Add(node.IsBsonUndefined ? BsonNull.Value : node);
            }

            return;
        }

        switch (node)
        {
            case BsonDocument document:
                if (document.TryGetValue(segments[index], out var child))
                {
                    Collect(child, segments, index + 1, values);
                }

                break;
            case BsonArray array:
                foreach (var element in array)
                {
                    Collect(element, segments, index, values);
                }

                break;
        }
    }

    /// <summary>
    ///     Compares two values the way MongoDB sorts them (type bracketing, numbers by value, strings by their UTF-8
    ///     bytes - no collation is configured on the entity collections).
    /// </summary>
    internal static int CompareValues(BsonValue a, BsonValue b)
    {
        if (a is BsonString sa && b is BsonString sb)
        {
            return CompareUtf8(sa.Value, sb.Value);
        }

        return a.CompareTo(b);
    }

    /// <summary>UTF-16 code units compared in the order of the UTF-8 encoding (surrogates sort above U+E000..U+FFFF).</summary>
    private static int CompareUtf8(string x, string y)
    {
        var length = Math.Min(x.Length, y.Length);
        for (var i = 0; i < length; i++)
        {
            var cx = x[i];
            var cy = y[i];
            if (cx == cy)
            {
                continue;
            }

            if (cx >= 0xD800 && cy >= 0xD800)
            {
                cx = (char)(cx >= 0xE000 ? cx - 0x800 : cx + 0x2000);
                cy = (char)(cy >= 0xE000 ? cy - 0x800 : cy + 0x2000);
            }

            return cx - cy;
        }

        return x.Length - y.Length;
    }

    /// <summary>One collection of the query and the implementing types that live in it.</summary>
    internal sealed record CollectionPlan(
        SingleOriginRtQuery<RtEntity> Query,
        string CollectionRoot,
        IReadOnlyList<RtCkId<CkTypeId>> CkTypeIds);

    private sealed record InterfaceRow(BsonDocument Document, BsonValue[] Keys, BsonValue Id);

    private sealed class RowComparer(IReadOnlyList<(string Path, bool Descending)> sortKeys)
        : IComparer<InterfaceRow>
    {
        public int Compare(InterfaceRow? x, InterfaceRow? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            for (var i = 0; i < sortKeys.Count; i++)
            {
                var c = CompareValues(x!.Keys[i], y!.Keys[i]);
                if (c != 0)
                {
                    return sortKeys[i].Descending ? -c : c;
                }
            }

            return x!.Id.CompareTo(y!.Id);
        }
    }
}
