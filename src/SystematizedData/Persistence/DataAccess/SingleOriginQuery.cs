using System.Collections.Generic;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public abstract class SingleOriginQuery<TEntity> : Query<TEntity> where TEntity : class, new()
{
    private readonly ICachedCollection<TEntity> _cachedCollection;

    protected internal SingleOriginQuery(ICachedCollection<TEntity> cachedCollection,
        string language = "en")
        : base(language)
    {
        _cachedCollection = cachedCollection;
    }

    public async Task<ResultSet<TEntity>> ExecuteQuery(IOctoSession octoSession, int? skip = null, int? take = null)
    {
        using var performanceMonitor = new PerformanceMonitor();
        var pipelineStageDefinitions = new List<IPipelineStageDefinition>();

        // In documentation, text search must be at first place
        AddTextFilterConstraintsToPipeline(pipelineStageDefinitions);
        // Filter for fields
        AddFilterConstraintsToPipeline(pipelineStageDefinitions);

        AddSortConstraintsToPipeline(pipelineStageDefinitions);

        performanceMonitor.SetCheckPoint("definitions created");

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
            var resultAggregate = _cachedCollection.Aggregate(octoSession, pipelineDefinition);
            var result = await resultAggregate.SingleOrDefaultAsync();
            var grouping = CalculateGrouping(result.Result);
            return new ResultSet<TEntity>(result, grouping);
        }
        else // Return result directly if there is no paging enabled
        {
            var pipelineDefinition = PipelineDefinition<TEntity, TEntity>.Create(pipelineStageDefinitions);

            var aggregate = _cachedCollection.Aggregate(octoSession, pipelineDefinition);
            var resultNoTotalCount = await aggregate.ToListAsync();
            var grouping = CalculateGrouping(resultNoTotalCount);
            return new ResultSet<TEntity>(resultNoTotalCount, resultNoTotalCount.Count, grouping);
        }
    }
}
