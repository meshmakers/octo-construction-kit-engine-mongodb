using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.Formulas;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

internal class MultipleOriginIndirectHierarchicalRtQuery : MultipleOriginIndirectHierarchicalRtQuery<RtEntity, RtEntity>
{
    internal MultipleOriginIndirectHierarchicalRtQuery(EntityCacheItem targetEntityCacheItem,
        IDatabaseContext databaseContext,
        string language, IEnumerable<OctoObjectId> rtIds, CkTypeId originCkId, string roleId,
        GraphDirections graphDirection, CkTypeId targetCkId)
        : base(targetEntityCacheItem, databaseContext, language, rtIds, originCkId, roleId, graphDirection,
            targetCkId)
    {
    }
}

internal class MultipleOriginIndirectHierarchicalRtQuery<TOriginEntity, TTargetEntity> : Query<TTargetEntity>
    where TOriginEntity : RtEntity
    where TTargetEntity : RtEntity, new()
{
    private readonly IDatabaseContext _databaseContext;
    private readonly GraphDirections _graphDirection;
    private readonly string _language;
    private readonly CkTypeId _originCkId;
    private readonly string _roleId;
    private readonly IEnumerable<OctoObjectId> _rtIds;
    private readonly CkTypeId _targetCkId;
    private readonly IEntityCacheItem _targetEntityCacheItem;

    internal MultipleOriginIndirectHierarchicalRtQuery(IEntityCacheItem targetEntityCacheItem,
        IDatabaseContext databaseContext,
        string language, IEnumerable<OctoObjectId> rtIds, CkTypeId originCkId, string roleId,
        GraphDirections graphDirection, CkTypeId targetCkId)
    {
        _targetEntityCacheItem = targetEntityCacheItem;
        _databaseContext = databaseContext;
        _language = language;
        _rtIds = rtIds;
        _originCkId = originCkId;
        _roleId = roleId;
        _graphDirection = graphDirection;
        _targetCkId = targetCkId;
    }

    internal async Task<MultipleOriginResultSet<TTargetEntity>> ExecuteQuery(IOctoSession session, int? skip,
        int? take)
    {
        if (skip.HasValue && !take.HasValue)
        {
            throw new PersistenceException("'skip' without 'take' is not possible.");
        }

        var connectFromRtIdField = "targetRtId";
        var connectToRtIdField = "originRtId";
        var connectToCkIdField = "originCkId";
        var @as = "_associations";

        switch (_graphDirection)
        {
            case GraphDirections.Inbound:
                connectFromRtIdField = "originRtId";
                connectToRtIdField = "originCkId";
                connectToCkIdField = "targetCkId";
                break;
            case GraphDirections.Outbound:
                break;
            default:
                throw new PersistenceException($"'{_graphDirection}' is not supported.");
        }


        var pipelineStageDefinitions = new List<IPipelineStageDefinition>();

        var associationFilter = new FilterDefinitionBuilder<RtAssociation>().Eq(x => x.AssociationRoleId, _roleId);

        var startWith = new ExpressionAggregateExpressionDefinition<RtEntity, BsonValue>(x => x.RtId.ToObjectId(), new ExpressionTranslationOptions());

        AddTextFilterConstraintsToPipeline(pipelineStageDefinitions);
        AddFilterConstraintsToPipeline(pipelineStageDefinitions);
        AddSortConstraintsToPipeline(pipelineStageDefinitions);

        var projectDefinition = (ProjectionDefinition<BsonDocument, BsonDocument>)
            new BsonDocument(@as,
                new BsonDocument("$sortArray",
                    new BsonDocument
                    {
                        {
                            "input",
                            new BsonDocument("$filter",
                                new BsonDocument
                                {
                                    { "input", "$" + @as },
                                    {
                                        "cond",
                                        new BsonDocument("$eq",
                                            new BsonArray
                                            {
                                                "$$this." + connectToCkIdField,
                                                _targetCkId.FullName
                                            })
                                    }
                                })
                        },
                        {
                            "sortBy",
                            new BsonDocument("depth", 1)
                        }
                    }));

        var aggregate = _databaseContext.GetRtCollection<RtEntity>(_originCkId).Aggregate(session)
            .Match(
                Builders<RtEntity>.Filter.And(Builders<RtEntity>.Filter.In(x => x.RtId, _rtIds)))
            .GraphLookup<RtAssociation, BsonValue, BsonValue, BsonValue, TTargetEntity, TTargetEntity[], BsonDocument>(
                from: _databaseContext.RtAssociations.GetMongoCollection(),
                connectFromField: connectFromRtIdField,
                connectToField: connectToRtIdField,
                startWith: startWith,
                depthField: "depth",
                @as: @as,
                options: new AggregateGraphLookupOptions<RtAssociation, TTargetEntity, BsonDocument>
                {
                    RestrictSearchWithMatch = associationFilter
                }
            )
            .Project(projectDefinition)
            .Unwind(@as, new AggregateUnwindOptions<BsonDocument> { PreserveNullAndEmptyArrays = true })
            .Lookup<BsonDocument, TTargetEntity, TTargetEntity, IEnumerable<TTargetEntity>, BsonDocument>(
                foreignCollection: _databaseContext.GetRtCollection<TTargetEntity>(_targetCkId).GetMongoCollection(),
                localField: @as + "." + connectToRtIdField,
                foreignField: "_id",
                lookupPipeline: pipelineStageDefinitions.Any()
                    ? PipelineDefinition<TTargetEntity, TTargetEntity>.Create(pipelineStageDefinitions)
                    : null,
                @as: @as)
            .Unwind(@as, new AggregateUnwindOptions<BsonDocument> { PreserveNullAndEmptyArrays = true })
            .Group<BsonDocument>(new BsonDocument
            {
                { "_id", "$_id" },
                { @as, new BsonDocument("$addToSet", "$" + @as) }
            });

        var aggregate2 = aggregate.ReplaceWith(
            (AggregateExpressionDefinition<BsonDocument, QueryMultipleResult<TTargetEntity>>)
            "{ _id: '$_id', totalCount: {$size: '$_associations' }, 'targets': '$_associations'}");


        if (skip.HasValue)
        {
            var query =
                "{ _id: '$_id', totalCount: {$size: '$_associations' }, 'targets': {'$slice': ['$_associations', " +
                skip + "," + take + "]}}";
            aggregate2 = aggregate.ReplaceWith(
                (AggregateExpressionDefinition<BsonDocument, QueryMultipleResult<TTargetEntity>>)query);
        }
        else if (take.HasValue)
        {
            var query =
                "{ _id: '$_id', totalCount: {$size: '$_associations' }, 'targets': {'$slice': ['$_associations', 0," +
                take + "]}}";
            aggregate2 = aggregate.ReplaceWith(
                (AggregateExpressionDefinition<BsonDocument, QueryMultipleResult<TTargetEntity>>)query);
        }

        var result = await aggregate2.ToListAsync();
        return new MultipleOriginResultSet<TTargetEntity>(result);
    }

    protected override bool IsAttributeNameValid(string attributeName)
    {
        return _targetEntityCacheItem.Attributes.TryGetValue(attributeName, out var _) ||
               attributeName == nameof(RtEntity.RtId) ||
               attributeName == nameof(RtEntity.RtCreationDateTime) ||
               attributeName == nameof(RtEntity.RtChangedDateTime) ||
               attributeName == nameof(RtEntity.RtWellKnownName);
    }

    protected override string ResolveAttributeName(string attributeName)
    {
        var baseResolve = base.ResolveAttributeName(attributeName);
        if (!string.IsNullOrEmpty(baseResolve))
        {
            return baseResolve;
        }

        if (typeof(RtEntity).GetProperty(attributeName) != null)
        {
            return attributeName.ToCamelCase();
        }

        return $"{Constants.AttributesName}.{attributeName.ToCamelCase()}";
    }

    protected override object? ResolveSearchAttributeValue(string attributeName, object? searchTerm, out bool isEnum)
    {
        if (searchTerm != null &&
            _targetEntityCacheItem.Attributes.TryGetValue(attributeName, out var attributeCacheItem))
        {
            var searchTermString = searchTerm.ToString();

            if (string.IsNullOrWhiteSpace(searchTermString))
            {
                isEnum = false;
                return null;
            }

            if (attributeCacheItem.SelectionValues != null)
            {
                var searchTermStringEnum = searchTermString.Replace("_", "");

                // Search for match in selection value
                var result = attributeCacheItem.SelectionValues.FirstOrDefault(x =>
                    string.Equals(x.Name, searchTermStringEnum, StringComparison.OrdinalIgnoreCase));
                if (result != null)
                {
                    isEnum = false;
                    return result.Key;
                }
            }

            var searchTermFormula = searchTerm.ToString()?.StartsWith("@") ?? false ? searchTermString.Substring(1) : null;
            if (!string.IsNullOrWhiteSpace(searchTermFormula))
            {
                var expression = new OctoExpression(searchTermFormula);
                var result = expression.calculate();

                if (double.IsNegativeInfinity(result))
                {
                    isEnum = false;
                    return null;
                }

                if (!double.IsNaN(result))
                {
                    switch (attributeCacheItem.AttributeValueType)
                    {
                        case AttributeValueTypes.DateTime:
                            isEnum = false;
                            return new DateTime((long)result);
                    }
                }
                else
                {
                    throw new OperationFailedException($"Term '{searchTerm}' cannot be evaluated by formula.");
                }
            }

            // Change to the type of attribute
            isEnum = false;
            return RtEntity.ConvertAttributeValue(attributeCacheItem.AttributeValueType, searchTerm);
        }

        return base.ResolveSearchAttributeValue(attributeName, searchTerm, out isEnum);
    }
}