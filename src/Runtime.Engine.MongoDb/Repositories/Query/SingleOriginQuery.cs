using Meshmakers.Common.Metrics.Context;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;

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

        AddPostStagesToPipeline(pipelineStageDefinitions);

        meter.SetCheckpoint("definitions created");

        var enrichmentStages = GetEnrichmentStageDefinitions();

        if (enrichmentStages.Count > 0 && (skip.HasValue || take.HasValue))
        {
            // Include mode with pagination: run separate count and page queries.
            // Navigation lookups (enrichment) run only on the paginated subset
            // instead of all matching documents, providing significant performance gains.

            // Count pipeline: base stages → $count
            var countPipelineStages = new List<IPipelineStageDefinition>(pipelineStageDefinitions);
            countPipelineStages.Add(PipelineStageDefinitionBuilder.Count<TEntity>());
            var countPipeline =
                PipelineDefinition<TEntity, AggregateCountResult>.Create(countPipelineStages);

            var countResult = await _mongoDbDataSourceCollection.Aggregate(octoSession, countPipeline)
                .SingleOrDefaultAsync();

            // Page pipeline: base stages → $skip → $limit → enrichment stages
            var pagePipelineStages = new List<IPipelineStageDefinition>(pipelineStageDefinitions);
            if (skip.HasValue)
            {
                pagePipelineStages.Add(PipelineStageDefinitionBuilder.Skip<TEntity>(skip.Value));
            }

            if (take.HasValue)
            {
                pagePipelineStages.Add(PipelineStageDefinitionBuilder.Limit<TEntity>(take.Value));
            }

            foreach (var stage in enrichmentStages)
            {
                pagePipelineStages.Add(stage);
            }

            var pagePipeline = PipelineDefinition<TEntity, TEntity>.Create(pagePipelineStages);
            var pageResult = await _mongoDbDataSourceCollection.Aggregate(octoSession, pagePipeline)
                .ToListAsync();

            var aggregations = CalculateAggregations(pageResult);
            return new ResultSet<TEntity>(pageResult, countResult?.Count ?? 0,
                aggregations.Item1, aggregations.Item2);
        }

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
