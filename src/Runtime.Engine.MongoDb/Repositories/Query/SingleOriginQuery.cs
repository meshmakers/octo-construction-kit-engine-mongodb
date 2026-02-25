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

        var aggregations = CalculateAggregations(result);
        return new ResultSet<TEntity>(result, totalCount, aggregations.Item1, aggregations.Item2);
    }

    public async Task<ResultSet<TEntity>> ExecuteQuery(IOctoSession octoSession, int? skip = null, int? take = null)
    {
        using var meter = _metricsContext.CreateRuntimeMeter();
        var pipelineStageDefinitions = new List<IPipelineStageDefinition>();

        // In documentation, text search must be the first place
        AddPreStagesToPipelines(pipelineStageDefinitions);
        // Filter for fields
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions != null)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(filterDefinitions));
        }

        var enrichmentStages = GetEnrichmentStageDefinitions();

        if (enrichmentStages.Count > 0 && (skip.HasValue || take.HasValue))
        {
            // Path 1: $facet optimization for queries with enrichment stages and pagination.
            // Uses a single $facet pipeline for both count and page results:
            // - Count branch: no sort needed, just $count
            // - Page branch: sort + $skip + $limit
            // Enrichment (navigation lookups) runs as a separate lightweight query
            // on paginated entities only.

            // Add only association stages (no sort) to base pipeline
            AddPrePaginationPostStagesToPipeline(pipelineStageDefinitions);

            meter.SetCheckpoint("definitions created");

            // Build $facet sub-pipelines
            var sortStages = CreateSortStageDefinitions();

            var pagingSubPipeline = new List<IPipelineStageDefinition>();
            pagingSubPipeline.AddRange(sortStages); // Sort only in page branch
            if (skip.HasValue)
            {
                pagingSubPipeline.Add(PipelineStageDefinitionBuilder.Skip<TEntity>(skip.Value));
            }

            if (take.HasValue)
            {
                pagingSubPipeline.Add(PipelineStageDefinitionBuilder.Limit<TEntity>(take.Value));
            }

            var countSubPipeline = new List<IPipelineStageDefinition>
            {
                PipelineStageDefinitionBuilder.Count<TEntity>() // No sort needed for count
            };

            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Facet<TEntity, QueryResult<TEntity>>(
                new List<AggregateFacet<TEntity>>([
                    new AggregateFacet<TEntity, TEntity>(
                        nameof(QueryResult<TEntity>.Result).ToCamelCase(),
                        PipelineDefinition<TEntity, TEntity>.Create(pagingSubPipeline)),
                    new AggregateFacet<TEntity, AggregateCountResult>(
                        nameof(QueryResult<TEntity>.TotalCount).ToCamelCase(),
                        PipelineDefinition<TEntity, AggregateCountResult>.Create(countSubPipeline))
                ])));

            var facetPipeline =
                PipelineDefinition<TEntity, QueryResult<TEntity>>.Create(pipelineStageDefinitions);
            var facetResult = await _mongoDbDataSourceCollection.Aggregate(octoSession, facetPipeline)
                .SingleOrDefaultAsync();

            meter.SetCheckpoint("facet executed");

            var count = facetResult?.TotalCount.FirstOrDefault()?.Count ?? 0;
            var pageEntities = facetResult?.Result?.ToList() ?? [];

            // Run enrichment on paginated entities only
            if (pageEntities.Count > 0)
            {
                var enrichmentPipelineStages = new List<IPipelineStageDefinition>();

                // Match only paginated entities by ID
                enrichmentPipelineStages.Add(PipelineStageDefinitionBuilder.Match(
                    CreateIdInFilter(pageEntities)));

                // Re-apply sort to maintain page order
                enrichmentPipelineStages.AddRange(sortStages);

                // Add enrichment stages ($lookup for navigation data)
                foreach (var stage in enrichmentStages)
                {
                    enrichmentPipelineStages.Add(stage);
                }

                var enrichmentPipeline =
                    PipelineDefinition<TEntity, TEntity>.Create(enrichmentPipelineStages);
                pageEntities = await _mongoDbDataSourceCollection.Aggregate(octoSession, enrichmentPipeline)
                    .ToListAsync();

                meter.SetCheckpoint("enrichment executed");
            }

            var aggregations = CalculateAggregations(pageEntities);
            return new ResultSet<TEntity>(pageEntities, count,
                aggregations.Item1, aggregations.Item2);
        }

        // For Path 2 and 3, add full post-stages (associations + sort) as before
        AddPostStagesToPipeline(pipelineStageDefinitions);

        meter.SetCheckpoint("definitions created");

        if (skip.HasValue || take.HasValue)
        {
            var pagingPipelineStageDefinitions = new List<IPipelineStageDefinition>();
            var countPipelineStageDefinitions = new List<IPipelineStageDefinition>();

            if (skip.HasValue)
            {
                pagingPipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Skip<TEntity>(skip.Value));
            }

            if (take.HasValue)
            {
                pagingPipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Limit<TEntity>(take.Value));
            }

            countPipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Count<TEntity>());

            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Facet<TEntity, QueryResult<TEntity>>(
                new List<AggregateFacet<TEntity>>([
                    new AggregateFacet<TEntity, TEntity>(nameof(QueryResult<TEntity>.Result).ToCamelCase(),
                        PipelineDefinition<TEntity, TEntity>.Create(
                            pagingPipelineStageDefinitions)),
                    new AggregateFacet<TEntity, AggregateCountResult>(
                        nameof(QueryResult<TEntity>.TotalCount).ToCamelCase(),
                        PipelineDefinition<TEntity, AggregateCountResult>
                            .Create(countPipelineStageDefinitions))
                ])));

            var pipelineDefinition = PipelineDefinition<TEntity, QueryResult<TEntity>>.Create(pipelineStageDefinitions);
            var resultAggregate = _mongoDbDataSourceCollection.Aggregate(octoSession, pipelineDefinition);
            var result = await resultAggregate.SingleOrDefaultAsync();
            var aggregations = CalculateAggregations(result.Result);
            return new ResultSet<TEntity>(result.Result, result.TotalCount.FirstOrDefault()?.Count ?? 0,
                aggregations.Item1, aggregations.Item2);
        }
        else // Return result directly if there is no paging enabled
        {
            // Add enrichment stages for the non-paginated case
            foreach (var stage in enrichmentStages)
            {
                pipelineStageDefinitions.Add(stage);
            }

            var pipelineDefinition = PipelineDefinition<TEntity, TEntity>.Create(pipelineStageDefinitions);

            var aggregate = _mongoDbDataSourceCollection.Aggregate(octoSession, pipelineDefinition);
            var resultNoTotalCount = await aggregate.ToListAsync();
            var aggregations = CalculateAggregations(resultNoTotalCount);
            return new ResultSet<TEntity>(resultNoTotalCount, resultNoTotalCount.Count, aggregations.Item1,
                aggregations.Item2);
        }
    }
}
