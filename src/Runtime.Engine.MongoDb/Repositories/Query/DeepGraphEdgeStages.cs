using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.Builders;

using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

/// <summary>
///     Shared tail of the deep-graph queries (AB#5003). Given a pipeline whose current document
///     carries a <c>uniqueEntities</c> array of <c>{ rtId, ckTypeId }</c> pairs, appends the stages
///     that collect the associations of ANY role whose origin and target both lie within that node
///     set, and group them into one <see cref="RtDeepGraphQueryResult"/> per node — including a
///     self-entry for nodes with no outbound edge, so every collected entity produces a row.
///     Extracted verbatim from <see cref="MultipleOriginHierarchicalDeepRtGraphQuery"/> so the
///     hierarchical (ParentChild) path and the directed role-set path share one byte-identical shape.
/// </summary>
internal static class DeepGraphEdgeStages
{
    internal static void AppendEdgeCollectionAndGroup(IList<IPipelineStageDefinition> pipelineStageDefinitions,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource, bool includeArchivedEntities)
    {
        var lookupPipelineFilter = OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.Or(
            OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.And(
                OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.In(
                    "$originRtId",
                    "$$uniqueEntities.rtId"),
                OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.In(
                    "$originCkTypeId",
                    "$$uniqueEntities.ckTypeId")
            ),
            OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.And(
                OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.In(
                    "$targetRtId",
                    "$$uniqueEntities.rtId"),
                OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.In(
                    "$targetCkTypeId",
                    "$$uniqueEntities.ckTypeId")
            )
        );

        var lookupPipeline = PipelineDefinition<RtAssociation, BsonDocument>.Create([
            OctoPipelineStageBuilder.Match(
                OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.Expression(
                    lookupPipelineFilter
                )),
        ]);

        if (!includeArchivedEntities)
        {
            lookupPipeline = PipelineDefinition<RtAssociation, BsonDocument>.Create([
                OctoPipelineStageBuilder.Match(
                    OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.Expression(
                        OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.And(
                            OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.Neq("rtState",
                                OctoBuilder<RtAssociation, BsonDocument>.AggregateOperators.Int32(1)),
                            lookupPipelineFilter
                        ))),
            ]);
        }

        pipelineStageDefinitions.Add(
            PipelineStageDefinitionBuilder
                .Lookup<BsonDocument, RtAssociation, BsonDocument, IEnumerable<BsonDocument>, BsonDocument>(
                    foreignCollection: mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations
                        .GetMongoCollection(),
                    let: new BsonDocument { { "uniqueEntities", "$uniqueEntities" } },
                    lookupPipeline: lookupPipeline,
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
    }
}
