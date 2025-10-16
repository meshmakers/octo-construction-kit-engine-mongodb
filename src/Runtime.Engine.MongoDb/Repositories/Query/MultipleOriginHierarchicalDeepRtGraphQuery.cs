using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class RtFieldFilterResolver : FieldFilterResolver<RtDeepGraphQueryResult>;

internal class MultipleOriginHierarchicalDeepRtGraphQuery : Query<RtDeepGraphQueryResult>
{
    private readonly IMongoDbRepositoryDataSource _mongoDbRepositoryDataSource;
    private readonly CkTypeGraph _originCkTypeGraph;
    private readonly RtCkId<CkAssociationRoleId> _roleId;
    private readonly IEnumerable<OctoObjectId> _rtIds;
    private readonly List<IPipelineStageDefinition> _geospatialFilters;

    internal MultipleOriginHierarchicalDeepRtGraphQuery(
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource,
        string language, IEnumerable<OctoObjectId> rtIds, CkTypeGraph originCkTypeGraph,
        RtCkId<CkAssociationRoleId> roleId)
        : base(new RtFieldFilterResolver(), language)
    {
        _mongoDbRepositoryDataSource = mongoDbRepositoryDataSource;
        _rtIds = rtIds;
        _originCkTypeGraph = originCkTypeGraph;
        _roleId = roleId;
        _geospatialFilters = new List<IPipelineStageDefinition>();
    }

    protected override void AddPreStagesToPipelines(IList<IPipelineStageDefinition> pipelineStageDefinitions)
    {
        _geospatialFilters.ForEach(pipelineStageDefinitions.Add);

        base.AddPostStagesToPipeline(pipelineStageDefinitions);
    }

    /// <summary>
    /// Add a geospatial filters to the query
    /// </summary>
    /// <param name="geospatialFilters">Filters to add</param>
    public void AddGeospatialFilters(ICollection<GeospatialFilter>? geospatialFilters)
    {
        if (geospatialFilters == null)
        {
            return;
        }

        foreach (var geospatialFilter in geospatialFilters)
        {
            var resolvedAttributeName = _fieldFilterResolver.ResolveAttributePath(geospatialFilter.AttributeName);
            if (string.IsNullOrWhiteSpace(resolvedAttributeName))
            {
                throw OperationFailedException.AttributePathResolutionFailed(geospatialFilter.AttributeName);
            }

            if (geospatialFilter is NearGeospatialFilter nearGeospatialFilter)
            {
                GeoJsonPoint<GeoJsonCoordinates> point = nearGeospatialFilter.Point.ToGeoJsonPoint();

                _geospatialFilters.Add(OctoPipelineStageBuilder.GeoNear<RtEntity, GeoJsonCoordinates>(
                    resolvedAttributeName, point, nearGeospatialFilter.MinDistance, nearGeospatialFilter.MaxDistance));
            }
        }
    }

    internal async Task<ResultSet<RtDeepGraphQueryResult>> ExecuteQuery(IOctoSession session, int? skip = null,
        int? take = null)
    {
        var pipelineStageDefinitions = new List<IPipelineStageDefinition>();

        AddPreStagesToPipelines(pipelineStageDefinitions);
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions != null)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(filterDefinitions));
        }

        AddPostStagesToPipeline(pipelineStageDefinitions);

        pipelineStageDefinitions.Add(
            PipelineStageDefinitionBuilder.Match(
                Builders<RtEntity>.Filter.And(Builders<RtEntity>.Filter.In(x => x.RtId, _rtIds))));
        pipelineStageDefinitions.Add(
            PipelineStageDefinitionBuilder
                .GraphLookup<RtEntity, RtAssociation, BsonValue, BsonValue, BsonValue, RtEntity, RtEntity[],
                    BsonDocument>(
                    _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations.GetMongoCollection(),
                    "originRtId",
                    "targetRtId",
                    "$_id",
                    @as: "_associations",
                    depthField: "depth",
                    options: new AggregateGraphLookupOptions<RtAssociation, RtEntity, BsonDocument>
                    {
                        RestrictSearchWithMatch =
                            new FilterDefinitionBuilder<RtAssociation>().Eq(x => x.AssociationRoleId, _roleId)
                    }
                ));

        pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Project(
            OctoBuilder<BsonDocument, BsonDocument>.Projection.SingleField(
                "_associations",
                OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Condition(
                    OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Gt(
                        OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Size("$_associations"),
                        OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Int32(0)),
                    OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.SortArray("$_associations",
                        new BsonDocument("depth", 1)),
                    OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Array([
                        new BsonDocument
                        {
                            { "originRtId", "$_id" },
                            { "originCkTypeId", "$ckTypeId" },
                            { "targetRtId", BsonNull.Value },
                            { "targetCkTypeId", BsonNull.Value },
                            { "depth", 0 }
                        }
                    ])
                ))
        ));

        pipelineStageDefinitions.Add(OctoPipelineStageBuilder.AddFields<BsonDocument, BsonDocument>(
            OctoBuilder<BsonDocument, BsonDocument>.Fields.Set("entities",
                OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Reduce("$_associations", new BsonArray(),
                    OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.ConcatArrays("$$value",
                        new BsonArray
                        {
                            new BsonDocument
                            {
                                { "rtId", "$$this.targetRtId" }, { "ckTypeId", "$$this.targetCkTypeId" }
                            },
                            new BsonDocument
                            {
                                { "rtId", "$$this.originRtId" }, { "ckTypeId", "$$this.originCkTypeId" }
                            }
                        }))
            )
        ));
        pipelineStageDefinitions.Add(OctoPipelineStageBuilder.AddFields<BsonDocument, BsonDocument>(
            OctoBuilder<BsonDocument, BsonDocument>.Fields.Set("uniqueEntities",
                OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Reduce(
                    OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Filter(
                        "$entities",
                        "entity",
                        OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Neq(
                            OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Field("$$entity.rtId"),
                            OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Null()
                        )
                    ),
                    new BsonArray(),
                    OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Condition(
                        OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.In(
                            OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Document(
                                new BsonDocument { { "rtId", "$$this.rtId" }, { "ckTypeId", "$$this.ckTypeId" } }),
                            OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Field("$$value")
                        ),
                        OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Field("$$value"),
                        OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.ConcatArrays("$$value",
                            new BsonArray
                            {
                                new BsonDocument { { "rtId", "$$this.rtId" }, { "ckTypeId", "$$this.ckTypeId" } }
                            })))
            )));
        pipelineStageDefinitions.Add(
            PipelineStageDefinitionBuilder
                .Lookup<BsonDocument, RtAssociation, BsonDocument, IEnumerable<BsonDocument>, BsonDocument>(
                    foreignCollection: _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations
                        .GetMongoCollection(),
                    let: new BsonDocument { { "uniqueEntities", "$uniqueEntities" } },
                    lookupPipeline: PipelineDefinition<RtAssociation, BsonDocument>.Create(new[]
                    {
                        OctoPipelineStageBuilder.Match(
                            OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.Expression(
                                OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.Or(
                                    OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.And(
                                        OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.In("$originRtId",
                                            "$$uniqueEntities.rtId"),
                                        OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.In(
                                            "$originCkTypeId",
                                            "$$uniqueEntities.ckTypeId")
                                    ),
                                    OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.And(
                                        OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.In("$targetRtId",
                                            "$$uniqueEntities.rtId"),
                                        OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.In(
                                            "$targetCkTypeId",
                                            "$$uniqueEntities.ckTypeId")
                                    )
                                )
                            ))
                    }),
                    @as: "matchingAssociations")
        );
        pipelineStageDefinitions.Add(OctoPipelineStageBuilder.AddFields<BsonDocument, BsonDocument>(
            OctoBuilder<BsonDocument, BsonDocument>.Fields.Set("associations",
                OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Filter(
                    "$matchingAssociations",
                    "association",
                    OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.And(
                        OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.And(
                            OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.In("$$association.originRtId",
                                "$uniqueEntities.rtId"),
                            OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.In(
                                "$$association.originCkTypeId",
                                "$uniqueEntities.ckTypeId")
                        ),
                        OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.And(
                            OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.In("$$association.targetRtId",
                                "$uniqueEntities.rtId"),
                            OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.In(
                                "$$association.targetCkTypeId",
                                "$uniqueEntities.ckTypeId")
                        )
                    )
                ))));

        pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Project(
            OctoBuilder<BsonDocument, BsonDocument>.Projection.SingleField(
                "associations",
                OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.ConcatArrays("$associations",
                    OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Map(
                        OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Filter(
                            "$uniqueEntities",
                            "uniqueEntity",
                            OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Not(
                                OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.In(
                                    OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.MergeObjects(
                                        OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Document(
                                            new BsonDocument { { "originRtId", "$$uniqueEntity.rtId" }, }),
                                        OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Document(
                                            new BsonDocument { { "originCkTypeId", "$$uniqueEntity.ckTypeId" } })
                                    ),
                                    OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Map("$associations",
                                        "association",
                                        OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.MergeObjects(
                                            OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Document(
                                                new BsonDocument { { "originRtId", "$$association.originRtId" }, }),
                                            OctoBuilder<BsonDocument, BsonDocument>.AggregateOperators.Document(
                                                new BsonDocument
                                                {
                                                    { "originCkTypeId", "$$association.originCkTypeId" }
                                                })))
                                )
                            )
                        ),
                        "uniqueEntity",
                        new BsonDocument
                        {
                            { "originRtId", "$$uniqueEntity.rtId" }, { "originCkTypeId", "$$uniqueEntity.ckTypeId" }
                        })
                ))));

        pipelineStageDefinitions.Add(
            PipelineStageDefinitionBuilder.Unwind<BsonDocument, BsonDocument>("associations"));
        pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Group<BsonDocument, RtDeepGraphQueryResult>(
            new BsonDocument
            {
                {
                    "_id",
                    new BsonDocument
                    {
                        { "rtId", "$associations.originRtId" }, { "ckTypeId", "$associations.originCkTypeId" }
                    }
                },
                {
                    "associations",
                    new BsonDocument
                    {
                        {
                            "$push",
                            new BsonDocument
                            {
                                {
                                    "$cond",
                                    new BsonDocument
                                    {
                                        {
                                            "if",
                                            new BsonDocument
                                            {
                                                {
                                                    "$gte", new BsonArray { "$associations.targetRtId", BsonNull.Value }
                                                }
                                            }
                                        },
                                        {
                                            "then",
                                            new BsonDocument
                                            {
                                                { "associationId", "$associations._id" },
                                                { "associationRoleId", "$associations.associationRoleId" },
                                                { "attributes", "$associations.attributes" },
                                                { "targetRtId", "$associations.targetRtId" },
                                                { "targetCkTypeId", "$associations.targetCkTypeId" },
                                                { "targetCkAttributeIds", "$associations.targetCkAttributeIds" }
                                            }
                                        },
                                        { "else", "$$REMOVE" }
                                    }
                                }
                            }
                        }
                    }
                }
            }));

        if (skip.HasValue || take.HasValue)
        {
            var pagingPipelineStageDefinitions = new List<IPipelineStageDefinition>();
            var countPipelineStageDefinitions = new List<IPipelineStageDefinition>();

            if (skip.HasValue)
            {
                pagingPipelineStageDefinitions.Add(
                    PipelineStageDefinitionBuilder.Skip<RtDeepGraphQueryResult>(skip.Value));
            }

            if (take.HasValue)
            {
                pagingPipelineStageDefinitions.Add(
                    PipelineStageDefinitionBuilder.Limit<RtDeepGraphQueryResult>(take.Value));
            }

            countPipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Count<RtDeepGraphQueryResult>());

            pipelineStageDefinitions.Add(
                PipelineStageDefinitionBuilder.Facet<RtDeepGraphQueryResult, QueryResult<RtDeepGraphQueryResult>>(
                    new List<AggregateFacet<RtDeepGraphQueryResult>>([
                        new AggregateFacet<RtDeepGraphQueryResult, RtDeepGraphQueryResult>(
                            nameof(QueryResult<RtDeepGraphQueryResult>.Result).ToCamelCase(),
                            PipelineDefinition<RtDeepGraphQueryResult, RtDeepGraphQueryResult>.Create(
                                pagingPipelineStageDefinitions)),
                        new AggregateFacet<RtDeepGraphQueryResult, AggregateCountResult>(
                            nameof(QueryResult<RtDeepGraphQueryResult>.TotalCount).ToCamelCase(),
                            PipelineDefinition<RtDeepGraphQueryResult, AggregateCountResult>
                                .Create(countPipelineStageDefinitions)),
                    ])));

            var pipelineDefinition =
                PipelineDefinition<RtEntity, QueryResult<RtDeepGraphQueryResult>>.Create(pipelineStageDefinitions);
            var resultAggregate = _mongoDbRepositoryDataSource
                .GetRtDatabaseCollection<RtEntity>(_originCkTypeGraph).Aggregate(session, pipelineDefinition);
            QueryResult<RtDeepGraphQueryResult>? result = await resultAggregate.SingleOrDefaultAsync();
            var aggregations = CalculateAggregations(result.Result);
            return new ResultSet<RtDeepGraphQueryResult>(result.Result, result.TotalCount.FirstOrDefault()?.Count ?? 0,
                aggregations.Item1, aggregations.Item2);
        }
        else // Return result directly if there is no paging enabled
        {
            var pipelineDefinition =
                PipelineDefinition<RtEntity, RtDeepGraphQueryResult>.Create(pipelineStageDefinitions);

            var aggregate = _mongoDbRepositoryDataSource
                .GetRtDatabaseCollection<RtEntity>(_originCkTypeGraph).Aggregate(session, pipelineDefinition);
            var resultNoTotalCount = await aggregate.ToListAsync();
            var aggregations = CalculateAggregations(resultNoTotalCount);
            return new ResultSet<RtDeepGraphQueryResult>(resultNoTotalCount, resultNoTotalCount.Count,
                aggregations.Item1, aggregations.Item2);
        }
    }
}
