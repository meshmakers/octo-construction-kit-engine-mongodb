using Meshmakers.Common.Metrics.Context;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

public abstract class SingleOriginQuery<TKey, TEntity> : Query<TEntity>
    where TEntity : class, new()
    where TKey : notnull
{
    private readonly IMetricsContext _metricsContext;
    private readonly IMongoDbDataSourceCollection<TKey, TEntity> _mongoDbDataSourceCollection;

    protected internal SingleOriginQuery(IMetricsContext metricsContext,
        IMongoDbDataSourceCollection<TKey, TEntity> mongoDbDataSourceCollection,
        string language = "en")
        : base(language)
    {
        _metricsContext = metricsContext;
        _mongoDbDataSourceCollection = mongoDbDataSourceCollection;
    }

    public async Task<ResultSet<TEntity>> ExecuteQuery(IOctoSession octoSession, int? skip = null, int? take = null)
    {
        using var meter = _metricsContext.CreateRuntimeMeter();
        var pipelineStageDefinitions = new List<IPipelineStageDefinition>();

        // In documentation, text search must be at first place
        AddTextFilterConstraintsToPipeline(pipelineStageDefinitions);
        // Filter for fields
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions != null)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(filterDefinitions));
        }

        AddSortConstraintsToPipeline(pipelineStageDefinitions);

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
                new List<AggregateFacet<TEntity>>(new AggregateFacet<TEntity>[]
                {
                    new AggregateFacet<TEntity, TEntity>(nameof(QueryResult<TEntity>.Result).ToCamelCase(),
                        PipelineDefinition<TEntity, TEntity>.Create(
                            pagingPipelineStageDefinitions)),
                    new AggregateFacet<TEntity, AggregateCountResult>(
                        nameof(QueryResult<TEntity>.TotalCount).ToCamelCase(),
                        PipelineDefinition<TEntity, AggregateCountResult>
                            .Create(countPipelineStageDefinitions))
                })));

            var pipelineDefinition = PipelineDefinition<TEntity, QueryResult<TEntity>>.Create(pipelineStageDefinitions);
            var resultAggregate = _mongoDbDataSourceCollection.Aggregate(octoSession, pipelineDefinition);
            var result = await resultAggregate.SingleOrDefaultAsync();
            var grouping = CalculateGrouping(result.Result);
            return new ResultSet<TEntity>(result.Result, result.TotalCount.FirstOrDefault()?.Count ?? 0, grouping);
        }
        else // Return result directly if there is no paging enabled
        {
            var pipelineDefinition = PipelineDefinition<TEntity, TEntity>.Create(pipelineStageDefinitions);

            var aggregate = _mongoDbDataSourceCollection.Aggregate(octoSession, pipelineDefinition);
            var resultNoTotalCount = await aggregate.ToListAsync();
            var grouping = CalculateGrouping(resultNoTotalCount);
            return new ResultSet<TEntity>(resultNoTotalCount, resultNoTotalCount.Count, grouping);
        }
    }
}