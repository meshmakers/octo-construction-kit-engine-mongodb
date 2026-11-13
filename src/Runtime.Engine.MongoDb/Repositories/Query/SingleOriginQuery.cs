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
    /// </summary>
    internal IReadOnlyList<IPipelineStageDefinition> CreatePagePipelineStageDefinitions(int? skip, int? take)
    {
        var pipelineStageDefinitions = new List<IPipelineStageDefinition>();

        AddPagePreStagesToPipeline(pipelineStageDefinitions);
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions != null)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(filterDefinitions));
        }

        // Association existence and count filters, then the sort.
        AddPostStagesToPipeline(pipelineStageDefinitions);

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
    internal sealed record ExecutedPipeline(string Purpose, IReadOnlyList<BsonDocument> Stages, int DocumentCount);

    private void Observe<TOutput>(string purpose, IReadOnlyList<IPipelineStageDefinition> stages, int documentCount)
    {
        if (PipelineObserver == null)
        {
            return;
        }

        var rendered = stages.Count == 0
            ? []
            : PipelineDefinition<TEntity, TOutput>.Create(stages)
                .Render(new RenderArgs<TEntity>(_mongoDbDataSourceCollection.GetMongoCollection().DocumentSerializer,
                    _mongoDbDataSourceCollection.GetMongoCollection().Settings.SerializerRegistry))
                .Documents
                .ToList();
        PipelineObserver(new ExecutedPipeline(purpose, rendered, documentCount));
    }

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
        if (take is not 0)
        {
            var pageStages = CreatePagePipelineStageDefinitions(skip, take);
            pageEntities = await _mongoDbDataSourceCollection
                .Aggregate(octoSession, PipelineDefinition<TEntity, TEntity>.Create(pageStages)).ToListAsync();
            Observe<TEntity>("page", pageStages, pageEntities.Count);

            meter.SetCheckpoint("page executed");
        }

        if (enrichmentStages.Count > 0 && pageEntities.Count > 0)
        {
            // Enrichment (navigation lookups) on the entities of the page only, in page order.
            var enrichmentPipelineStages = new List<IPipelineStageDefinition>
            {
                PipelineStageDefinitionBuilder.Match(CreateIdInFilter(pageEntities))
            };
            enrichmentPipelineStages.AddRange(CreateSortStageDefinitions());
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
            totalCount = await ExecuteCount(octoSession);
        }
        else
        {
            totalCount = pageEntities.Count == 0 ? 0 : (skip ?? 0) + pageEntities.Count;
        }

        var pageAggregations = FinalizeResults(pageEntities);
        return new ResultSet<TEntity>(pageEntities, totalCount, pageAggregations.Item1, pageAggregations.Item2);
    }
}
