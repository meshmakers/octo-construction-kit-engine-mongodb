using Meshmakers.Common.Metrics.Context;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;

using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal abstract class SingleOriginQuery<TKey, TEntity> : Query<TEntity>
    where TEntity : class, new()
    where TKey : notnull
{
    private readonly IMetricsContext _metricsContext;
    private readonly IMongoDbDataSourceCollection<TKey, TEntity> _mongoDbDataSourceCollection;

    protected internal SingleOriginQuery(IMetricsContext metricsContext,
        IMongoDbDataSourceCollection<TKey, TEntity> mongoDbDataSourceCollection,
        FieldFilterResolver<TEntity> fieldFilterResolver, string language = "en")
        : base(fieldFilterResolver, language)
    {
        _metricsContext = metricsContext;
        _mongoDbDataSourceCollection = mongoDbDataSourceCollection;
    }

    /// <summary>
    /// The combined filter of this query (security pre-filter, pre-filters, field filters), or null when the
    /// query has none. Used by the interface query (AB#7019) to build its own pipelines on the same filter the
    /// type query would use.
    /// </summary>
    internal FilterDefinition<TEntity>? BuildMatchFilter() => CreateFilterDefinitions();

    /// <summary>
    /// The driver collection this query runs against.
    /// </summary>
    internal IMongoCollection<TEntity> GetMongoCollection() => _mongoDbDataSourceCollection.GetMongoCollection();

    /// <summary>
    /// Returns enrichment pipeline stages that should be applied post-pagination.
    /// Override in derived classes to provide navigation lookup stages for Include mode.
    /// </summary>
    internal virtual IReadOnlyList<IPipelineStageDefinition> GetEnrichmentStageDefinitions()
        => Array.Empty<IPipelineStageDefinition>();

    /// <summary>
    /// Adds pre-pagination post-stages (e.g., association existence checks) without sort.
    /// Override in derived classes that have association stages.
    /// </summary>
    protected virtual void AddPrePaginationPostStagesToPipeline(
        IList<IPipelineStageDefinition> pipelineStageDefinitions) { }

    /// <summary>
    /// Returns a filter matching only the specified entity IDs.
    /// Used for enrichment pipeline after $facet pagination.
    /// </summary>
    protected virtual FilterDefinition<TEntity> CreateIdInFilter(IEnumerable<TEntity> entities)
    {
        throw new NotSupportedException(
            "CreateIdInFilter must be overridden when enrichment stages are used with $facet optimization.");
    }

    /// <summary>
    /// Collects all matching entity IDs by running the base pipeline (match + existence check + sort)
    /// with a projection to _id only. Used to populate the query result cache.
    /// </summary>
    internal async Task<List<OctoObjectId>> CollectMatchingEntityIds(IOctoSession octoSession)
    {
        using var meter = _metricsContext.CreateRuntimeMeter();
        var pipelineStageDefinitions = new List<IPipelineStageDefinition>();

        AddPreStagesToPipelines(pipelineStageDefinitions);
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions != null)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(filterDefinitions));
        }

        // Add association existence check (pre-pagination stages)
        AddPrePaginationPostStagesToPipeline(pipelineStageDefinitions);

        // Add sort for consistent ordering
        var sortStages = CreateSortStageDefinitions();
        foreach (var sortStage in sortStages)
        {
            pipelineStageDefinitions.Add(sortStage);
        }

        // Project to IDs only
        pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Project<TEntity>(
            new BsonDocument("_id", 1)));

        meter.SetCheckpoint("collect ids pipeline created");

        var pipeline = PipelineDefinition<TEntity, BsonDocument>.Create(pipelineStageDefinitions);
        var cursor = _mongoDbDataSourceCollection.Aggregate(octoSession, pipeline);
        var results = await cursor.ToListAsync();

        meter.SetCheckpoint("collect ids executed");

        return results.Select(doc => new OctoObjectId(doc["_id"].AsObjectId.ToByteArray())).ToList();
    }

    /// <summary>
    ///     The pipeline of a count-only query (AB#8895): the stages that narrow the result set exactly as the item
    ///     query does - geospatial/text pre-stages, the <c>$match</c> of security, type and field filters, and the
    ///     association stages navigation filters need - followed by <c>$count</c>. No sort, no paging, no
    ///     enrichment lookup: the pipeline returns one count document and never a runtime entity.
    /// </summary>
    internal IReadOnlyList<IPipelineStageDefinition> CreateCountPipelineStageDefinitions()
    {
        var pipelineStageDefinitions = new List<IPipelineStageDefinition>();

        AddCountPreStagesToPipeline(pipelineStageDefinitions);
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions != null)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(filterDefinitions));
        }

        // Association existence and count filters narrow the result set; the enrichment lookups do not.
        AddPrePaginationPostStagesToPipeline(pipelineStageDefinitions);

        pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Count<TEntity>());
        return pipelineStageDefinitions;
    }

    /// <summary>
    ///     Counts the entities the item query would return, on the server and without loading any document
    ///     (AB#8895). The count equals <see cref="ResultSet{TEntity}.TotalCount" /> of <see cref="ExecuteQuery" />
    ///     for the same query; skip and take do not apply (the total count ignores paging).
    /// </summary>
    internal async Task<long> ExecuteCount(IOctoSession octoSession)
    {
        using var meter = _metricsContext.CreateRuntimeMeter();
        var stages = CreateCountPipelineStageDefinitions();
        var pipeline = PipelineDefinition<TEntity, AggregateCountResult>.Create(stages);

        meter.SetCheckpoint("count pipeline created");

        var countResult = await _mongoDbDataSourceCollection.Aggregate(octoSession, pipeline).FirstOrDefaultAsync();
        Observe<AggregateCountResult>("count", stages, countResult == null ? 0 : 1);

        meter.SetCheckpoint("count executed");

        // $count emits no document when nothing matches.
        return countResult?.Count ?? 0;
    }

    /// <summary>
    /// Executes enrichment pipeline on a given set of entity IDs.
    /// Used when serving pages from the query result cache.
    /// </summary>
    internal async Task<ResultSet<TEntity>> ExecuteEnrichmentForIds(
        IOctoSession octoSession, IReadOnlyList<OctoObjectId> entityIds, long totalCount)
    {
        if (entityIds.Count == 0)
        {
            return new ResultSet<TEntity>([], totalCount, null, null);
        }

        using var meter = _metricsContext.CreateRuntimeMeter();
        var enrichmentStages = GetEnrichmentStageDefinitions();
        var pipelineStages = new List<IPipelineStageDefinition>();

        // Match by IDs
        pipelineStages.Add(PipelineStageDefinitionBuilder.Match(
            Builders<TEntity>.Filter.In("_id", entityIds)));

        // Sort to maintain order
        pipelineStages.AddRange(CreateSortStageDefinitions());

        // Enrichment ($lookup for navigation data)
        foreach (var stage in enrichmentStages)
        {
            pipelineStages.Add(stage);
        }

        meter.SetCheckpoint("enrichment for ids pipeline created");

        var pipeline = PipelineDefinition<TEntity, TEntity>.Create(pipelineStages);
        var result = await _mongoDbDataSourceCollection.Aggregate(octoSession, pipeline).ToListAsync();

        meter.SetCheckpoint("enrichment for ids executed");

        var aggregations = FinalizeResults(result);
        return new ResultSet<TEntity>(result, totalCount, aggregations.Item1, aggregations.Item2);
    }

    /// <summary>
    ///     The stages that open the page pipeline (<see cref="CreatePagePipelineStageDefinitions" />). By default the
    ///     stages of <see cref="AddPreStagesToPipelines" />; a query whose pre-stages repeat the sort that
    ///     <see cref="AddPostStagesToPipeline" /> adds anyway overrides this without that sort, so that the page
    ///     pipeline sorts once, right before <c>$skip</c>/<c>$limit</c> (AB#8896).
    /// </summary>
    protected virtual void AddPagePreStagesToPipeline(IList<IPipelineStageDefinition> pipelineStageDefinitions)
        => AddPreStagesToPipelines(pipelineStageDefinitions);

    /// <summary>
    ///     The pipeline of one page (AB#8896): the stages that narrow the result set exactly as the count does
    ///     (pre-stages, the <c>$match</c> of security, type and field filters, the association stages navigation
    ///     filters need), then <c>$sort</c>, <c>$skip</c> and <c>$limit</c>. It returns at most
    ///     <paramref name="take" /> documents; the enrichment lookups of <see cref="GetEnrichmentStageDefinitions" />
    ///     are not part of it - they run afterwards on the page only. It has no <c>$facet</c> and no count.
    ///     With <paramref name="idTiebreak" /> the <c>$sort</c> is always there and total (AB#8960,
    ///     <see cref="Query{TEntity}.CreatePagingSortDefinition" />): the caller's keys, then <c>_id</c>. Without it the
    ///     page sorts by the caller's keys only, and not at all without a caller sort - the order before AB#8960, kept
    ///     for a sort whose order a legacy-shape index provides (<see cref="PagingTiebreakGate" />).
    /// </summary>
    internal IReadOnlyList<IPipelineStageDefinition> CreatePagePipelineStageDefinitions(int? skip, int? take,
        bool idTiebreak = true)
    {
        var pipelineStageDefinitions = new List<IPipelineStageDefinition>();

        AddPagePreStagesToPipeline(pipelineStageDefinitions);
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions != null)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(filterDefinitions));
        }

        // Association existence and count filters, then the sort of the page.
        AddPrePaginationPostStagesToPipeline(pipelineStageDefinitions);
        pipelineStageDefinitions.AddRange(CreatePageSortStageDefinitions(idTiebreak));

        if (skip is > 0)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Skip<TEntity>(skip.Value));
        }

        if (take.HasValue)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Limit<TEntity>(take.Value));
        }

        return pipelineStageDefinitions;
    }

    /// <summary>
    ///     Observes every pipeline <see cref="ExecuteQuery" /> runs: its purpose (<c>page</c>, <c>enrichment</c>,
    ///     <c>count</c>, <c>items</c>), its rendered stages and the number of documents it returned. Null in
    ///     production; set by tests (AB#8896) to prove which reads a query makes.
    /// </summary>
    internal Action<ExecutedPipeline>? PipelineObserver { get; set; }

    /// <summary>One pipeline run of <see cref="ExecuteQuery" />, reported to <see cref="PipelineObserver" />.</summary>
    internal sealed record ExecutedPipeline(string Purpose, IReadOnlyList<BsonDocument> Stages, int DocumentCount)
    {
        /// <summary>The index the read was hinted to (AB#9138), or null.</summary>
        public string? Hint { get; init; }

        /// <summary>The index of a hint the server refused before the read was repeated without it, or null.</summary>
        public string? RefusedHint { get; init; }
    }

    private void Observe<TOutput>(string purpose, IReadOnlyList<IPipelineStageDefinition> stages, int documentCount,
        string? hint = null, string? refusedHint = null)
    {
        if (PipelineObserver == null)
        {
            return;
        }

        PipelineObserver(new ExecutedPipeline(purpose, Render<TOutput>(stages), documentCount)
        {
            Hint = hint, RefusedHint = refusedHint
        });
    }

    /// <summary>The stages rendered as the server receives them.</summary>
    private IReadOnlyList<BsonDocument> Render<TOutput>(IReadOnlyList<IPipelineStageDefinition> stages) =>
        stages.Count == 0
            ? []
            : PipelineDefinition<TEntity, TOutput>.Create(stages)
                .Render(new RenderArgs<TEntity>(_mongoDbDataSourceCollection.GetMongoCollection().DocumentSerializer,
                    _mongoDbDataSourceCollection.GetMongoCollection().Settings.SerializerRegistry))
                .Documents
                .ToList();

    /// <summary>
    ///     True when the page pipeline is <c>$match</c> followed by its <c>$sort</c>: no stage before the match (a
    ///     <c>$geoNear</c>, a text search) and no association stage between the match and the sort. Only then is the
    ///     sort part of the query MongoDB plans on the collection, and only then can an index hint provide it.
    /// </summary>
    internal bool PageSortFollowsMatch()
    {
        var preStages = new List<IPipelineStageDefinition>();
        AddPagePreStagesToPipeline(preStages);
        var associationStages = new List<IPipelineStageDefinition>();
        AddPrePaginationPostStagesToPipeline(associationStages);
        return preStages.Count == 0 && associationStages.Count == 0;
    }

    /// <summary>
    ///     The index a sorted page is hinted to (AB#9138, <see cref="SortIndexHint" />), and the exact total when the
    ///     decision had to count it. The hint names the index that provides the caller's sort after the
    ///     <c>ckTypeId</c> equality, taken from the cached indexes of the collection
    ///     (<see cref="CollectionIndexCache" />). No hint - the planner decides as before - for a page with a skip below
    ///     <see cref="SortIndexHint.MinSkip" /> (the trial ends early there), without a sort, when the page pipeline has
    ///     stages around the match (<see cref="PageSortFollowsMatch" />), inside a transaction (a hint naming an index
    ///     dropped meanwhile would fail the command and abort the transaction, so there is no retry) and when no index
    ///     provides the sort.
    ///     <para>
    ///         When the filter has an index of its own (<see cref="SortIndexHint.HasFilterIndex" />), the planner can
    ///         read few matches through it and sort them faster than the hinted walk along the sort index reaches the
    ///         page. The page is then hinted only when at least <see cref="SortIndexHint.MinMatches" /> entities match:
    ///         counted exactly when the caller asked for the total (the count runs before the page and is reused, so it
    ///         costs nothing extra), otherwise with a count capped at that number.
    ///     </para>
    /// </summary>
    internal async Task<((string Name, BsonDocument Key)? Hint, long? ExactTotal)> ChoosePageHintAsync(IOctoSession octoSession, int? skip,
        IReadOnlyList<IPipelineStageDefinition> pageStages, bool includeTotalCount, bool idTiebreak = false)
    {
        if (!SortIndexHint.IsDeep(skip) || !HasSortDefinitions ||
            octoSession is IOctoSessionInternal { IsTransactionActive: true } || !PageSortFollowsMatch())
        {
            return (null, null);
        }

        var indexes = await CollectionIndexCache.GetAsync(_mongoDbDataSourceCollection.GetMongoCollection());
        // AB#8960: with the _id tiebreak the index must provide _id after the caller's keys too.
        var hint = SortIndexHint.Choose(indexes, PageSortKeys(idTiebreak));
        if (hint == null || !SortIndexHint.HasFilterIndex(indexes!, SortIndexHint.FilterFields(RenderMatch(pageStages)),
                FirstSortKeyPath))
        {
            return (hint, null);
        }

        if (SortIndexHint.MinMatches <= 0)
        {
            return (hint, null);
        }

        if (includeTotalCount)
        {
            var total = await ExecuteCount(octoSession);
            return (total >= SortIndexHint.MinMatches ? hint : null, total);
        }

        var capped = await ExecuteCappedCount(octoSession, SortIndexHint.MinMatches);
        return (capped >= SortIndexHint.MinMatches ? hint : null, null);
    }

    /// <summary>The filter of the page's leading <c>$match</c>, rendered; empty when the page has none.</summary>
    private BsonDocument RenderMatch(IReadOnlyList<IPipelineStageDefinition> pageStages)
    {
        var first = Render<TEntity>(pageStages.Take(1).ToList()).FirstOrDefault();
        return first != null && first.TryGetValue("$match", out var match) && match is BsonDocument filter
            ? filter
            : new BsonDocument();
    }

    /// <summary>
    ///     Counts the matching entities up to <paramref name="limit" /> (AB#9138): the count pipeline with a
    ///     <c>$limit</c> before its <c>$count</c>, so the server stops at the limit instead of counting every match.
    /// </summary>
    internal async Task<long> ExecuteCappedCount(IOctoSession octoSession, long limit)
    {
        var stages = CreateCountPipelineStageDefinitions().ToList();
        stages.Insert(stages.Count - 1, PipelineStageDefinitionBuilder.Limit<TEntity>(limit));
        var countResult = await _mongoDbDataSourceCollection
            .Aggregate(octoSession, PipelineDefinition<TEntity, AggregateCountResult>.Create(stages))
            .FirstOrDefaultAsync();
        Observe<AggregateCountResult>("capped count", stages, countResult == null ? 0 : 1);
        return countResult?.Count ?? 0;
    }

    /// <summary>
    ///     Reads one page, hinted by the key pattern of <paramref name="hint" /> when given, and returns the name of the
    ///     index the read was really hinted to and of a hint the server refused. A hint whose index no longer exists -
    ///     dropped by another process within the cache interval, or rebuilt under its name with other keys - fails with
    ///     a command error: the cached indexes of the collection are forgotten and the page is read once more without a
    ///     hint.
    /// </summary>
    private async Task<(List<TEntity> Entities, string? UsedHint, string? RefusedHint)> ReadPageAsync(
        IOctoSession octoSession,
        IReadOnlyList<IPipelineStageDefinition> pageStages, (string Name, BsonDocument Key)? hint)
    {
        var pipeline = PipelineDefinition<TEntity, TEntity>.Create(pageStages);
        if (hint == null)
        {
            return (await _mongoDbDataSourceCollection.Aggregate(octoSession, pipeline).ToListAsync(), null, null);
        }

        try
        {
            var options = new AggregateOptions { Hint = hint.Value.Key };
            return (await _mongoDbDataSourceCollection.Aggregate(octoSession, pipeline, options).ToListAsync(),
                hint.Value.Name, null);
        }
        catch (MongoCommandException exception) when (IsUnusableHint(exception))
        {
            CollectionIndexCache.Invalidate(_mongoDbDataSourceCollection.GetMongoCollection());
            return (await _mongoDbDataSourceCollection.Aggregate(octoSession, pipeline).ToListAsync(), null,
                hint.Value.Name);
        }
    }

    /// <summary>The server refused the hint: no index with its key pattern exists (any more).</summary>
    private static bool IsUnusableHint(MongoCommandException exception) =>
        exception.Code is 2 or 27 &&
        exception.ErrorMessage?.Contains("hint", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    ///     Runs the query. Without <paramref name="skip" /> and <paramref name="take" /> one pipeline returns all
    ///     matching entities and the total count is their number.
    ///     <para>
    ///         Paged (AB#8896), the query makes separate reads instead of one <c>$facet</c> that pushed every matching
    ///         document through the server: the page pipeline (<see cref="CreatePagePipelineStageDefinitions" />)
    ///         returns at most <paramref name="take" /> documents, the enrichment lookups run on those documents
    ///         only, and - when <paramref name="includeTotalCount" /> is true - the count pipeline of
    ///         <see cref="ExecuteCount" /> counts on the server without returning a document. The reads run one after
    ///         the other on the caller's session (a client session must not run operations concurrently); outside a
    ///         transaction they are separate reads, not one snapshot, so a write in between can make the total count
    ///         and the page disagree.
    ///     </para>
    /// </summary>
    /// <param name="octoSession">The session the reads run in.</param>
    /// <param name="skip">Number of entities to skip, or null.</param>
    /// <param name="take">Maximum number of entities to return, or null. Zero returns no entity without a read.</param>
    /// <param name="includeTotalCount">
    ///     Paged queries only: when false no count runs, and <see cref="ResultSet{TEntity}.TotalCount" /> is a lower
    ///     bound, not a count - <paramref name="skip" /> plus the number of returned entities, or 0 for an empty page.
    /// </param>
    public async Task<ResultSet<TEntity>> ExecuteQuery(IOctoSession octoSession, int? skip = null, int? take = null,
        bool includeTotalCount = true)
    {
        using var meter = _metricsContext.CreateRuntimeMeter();
        var enrichmentStages = GetEnrichmentStageDefinitions();

        if (!skip.HasValue && !take.HasValue)
        {
            // Not paged: one pipeline, the total count is the number of returned entities.
            var pipelineStageDefinitions = new List<IPipelineStageDefinition>();

            // In documentation, text search must be the first place
            AddPreStagesToPipelines(pipelineStageDefinitions);
            var filterDefinitions = CreateFilterDefinitions();
            if (filterDefinitions != null)
            {
                pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(filterDefinitions));
            }

            AddPostStagesToPipeline(pipelineStageDefinitions);
            pipelineStageDefinitions.AddRange(enrichmentStages);

            meter.SetCheckpoint("definitions created");

            var pipelineDefinition = PipelineDefinition<TEntity, TEntity>.Create(pipelineStageDefinitions);
            var resultNoTotalCount = await _mongoDbDataSourceCollection.Aggregate(octoSession, pipelineDefinition)
                .ToListAsync();
            Observe<TEntity>("items", pipelineStageDefinitions, resultNoTotalCount.Count);

            meter.SetCheckpoint("query executed");

            var aggregations = FinalizeResults(resultNoTotalCount);
            return new ResultSet<TEntity>(resultNoTotalCount, resultNoTotalCount.Count, aggregations.Item1,
                aggregations.Item2);
        }

        var pageEntities = new List<TEntity>();
        long? exactTotal = null;
        var idTiebreak = false;
        if (take is not 0)
        {
            // AB#8960: the _id tiebreak unless a legacy index provides the order of this sort.
            idTiebreak = await PagingTiebreakGate.IsEnabledAsync(_mongoDbDataSourceCollection.GetMongoCollection(),
                SortKeys);
            var pageStages = CreatePagePipelineStageDefinitions(skip, take, idTiebreak);
            // AB#9138: a deep sorted page spent its time in the planner's trial, not in the read - hint the index that
            // provides the sort.
            (var hint, exactTotal) =
                await ChoosePageHintAsync(octoSession, skip, pageStages, includeTotalCount, idTiebreak);
            (pageEntities, var usedHint, var refusedHint) = await ReadPageAsync(octoSession, pageStages, hint);
            Observe<TEntity>("page", pageStages, pageEntities.Count, usedHint, refusedHint);

            meter.SetCheckpoint("page executed");
        }

        if (enrichmentStages.Count > 0 && pageEntities.Count > 0)
        {
            // Enrichment (navigation lookups) on the entities of the page only, in page order: the same total sort
            // as the page pipeline, so that ties keep their page order.
            var enrichmentPipelineStages = new List<IPipelineStageDefinition>
            {
                PipelineStageDefinitionBuilder.Match(CreateIdInFilter(pageEntities))
            };
            enrichmentPipelineStages.AddRange(CreatePageSortStageDefinitions(idTiebreak));
            enrichmentPipelineStages.AddRange(enrichmentStages);

            pageEntities = await _mongoDbDataSourceCollection
                .Aggregate(octoSession, PipelineDefinition<TEntity, TEntity>.Create(enrichmentPipelineStages))
                .ToListAsync();
            Observe<TEntity>("enrichment", enrichmentPipelineStages, pageEntities.Count);

            meter.SetCheckpoint("enrichment executed");
        }

        long totalCount;
        if (includeTotalCount)
        {
            // The hint decision may have counted already (AB#9138); the count is not repeated.
            totalCount = exactTotal ?? await ExecuteCount(octoSession);
        }
        else
        {
            totalCount = pageEntities.Count == 0 ? 0 : (skip ?? 0) + pageEntities.Count;
        }

        var pageAggregations = FinalizeResults(pageEntities);
        return new ResultSet<TEntity>(pageEntities, totalCount, pageAggregations.Item1, pageAggregations.Item2);
    }
}
