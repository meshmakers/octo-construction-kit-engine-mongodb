using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.Formulas;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

internal class MultipleOriginHierarchicalRtQuery : MultipleOriginHierarchicalRtQuery<RtEntity, RtEntity>
{
    internal MultipleOriginHierarchicalRtQuery(EntityCacheItem targetEntityCacheItem,
        IDatabaseContext databaseContext,
        string language, IEnumerable<ObjectId> rtIds, string originCkId, string roleId,
        GraphDirections graphDirection, string targetCkId)
        : base(targetEntityCacheItem, databaseContext, language, rtIds, originCkId, roleId, graphDirection,
            targetCkId)
    {
    }
}

internal class MultipleOriginHierarchicalRtQuery<TOriginEntity, TTargetEntity> : Query<TTargetEntity>
    where TOriginEntity : RtEntity
    where TTargetEntity : RtEntity, new()
{
    private readonly IDatabaseContext _databaseContext;
    private readonly GraphDirections _graphDirection;
    private readonly string _language;
    private readonly string _originCkId;
    private readonly string _roleId;
    private readonly IEnumerable<ObjectId> _rtIds;
    private readonly string _targetCkId;
    private readonly EntityCacheItem _targetEntityCacheItem;

    internal MultipleOriginHierarchicalRtQuery(EntityCacheItem targetEntityCacheItem,
        IDatabaseContext databaseContext,
        string language, IEnumerable<ObjectId> rtIds, string originCkId, string roleId,
        GraphDirections graphDirection, string targetCkId)
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

        var innerLocalField = (FieldDefinition<RtAssociation>)"targetRtId";
        var connectToField = (FieldDefinition<RtAssociation, string>)"originRtId";

        switch (_graphDirection)
        {
            case GraphDirections.Inbound:
                innerLocalField = "originRtId";
                connectToField = "targetRtId";
                break;
            case GraphDirections.Outbound:
                break;
            default:
                throw new PersistenceException($"'{_graphDirection}' is not supported.");
        }

        var connectFromField = (FieldDefinition<RtEntity, string[]>)"_id";
        var @as = (FieldDefinition<BsonDocument, TTargetEntity[]>)"_associations";

        var pipelineStageDefinitions = new List<IPipelineStageDefinition>(new IPipelineStageDefinition[]
        {
            PipelineStageDefinitionBuilder.Match(
                Builders<RtAssociation>.Filter.Eq("associationRoleId", _roleId)),
            PipelineStageDefinitionBuilder.Lookup(
                _databaseContext.GetRtCollection<TTargetEntity>(_targetCkId).GetMongoCollection(),
                innerLocalField,
                "_id",
                (FieldDefinition<BsonDocument>)"target"),
            PipelineStageDefinitionBuilder.Unwind((FieldDefinition<BsonDocument>)"target"),
            PipelineStageDefinitionBuilder.ReplaceRoot<BsonDocument, TTargetEntity>("$target")
        });


        AddTextFilterConstraintsToPipeline(pipelineStageDefinitions);
        AddFilterConstraintsToPipeline(pipelineStageDefinitions);
        AddSortConstraintsToPipeline(pipelineStageDefinitions);


        var aggregate = _databaseContext.GetRtCollection<RtEntity>(_originCkId).Aggregate(session)
            .Match(
                Builders<RtEntity>.Filter.And(Builders<RtEntity>.Filter.In(x => x.RtId, _rtIds)))
            .Lookup(
                _databaseContext.RtAssociations.GetMongoCollection(),
                connectFromField,
                connectToField,
                PipelineDefinition<RtAssociation, TTargetEntity>.Create(pipelineStageDefinitions),
                @as
            );


        // In documentation, text search must be at first place
        //  aggregate = AddTextFilterConstraintsToPipeline(aggregate);
        //
        // if (filters.Any())
        // {
        //     var filterDefinition = Builders<TEntity>.Filter.Empty;
        //     if (filters.Any())
        //     {
        //         if (filters.Count == 1)
        //         {
        //             filterDefinition = filters.First();
        //         }
        //         else
        //         {
        //             filterDefinition = Builders<TEntity>.Filter.And(filters);
        //         }
        //     }
        //
        //     aggregate = aggregate.Match(filterDefinition);
        // }

        // aggregate1 = aggregate1.Lookup(
        //     _databaseContext.GetRtCollection<RtEntity>(_targetCkId).GetMongoCollection().CollectionNamespace
        //         .CollectionName,
        //     "rtassocs.targetRtId", "_id", "associations");
        // aggregate1 = aggregate1.Unwind("associations");
        // aggregate1 =
        //     aggregate1.ReplaceRoot<BsonDocument>("{ $mergeObjects: [{_originId: '$_id'}, '$associations']}");
        //

        // TODO: Filter for targets


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
    
    protected override object ResolveSearchAttributeValue(string attributeName, object searchTerm, out bool isEnum)
    {
        if (searchTerm != null &&
            _targetEntityCacheItem.Attributes.TryGetValue(attributeName, out var attributeCacheItem))
        {
            if (attributeCacheItem.SelectionValues != null)
            {
                var searchTermString = searchTerm.ToString().Replace("_", "");

                // Search for match in selection value
                var result = attributeCacheItem.SelectionValues.FirstOrDefault(x =>
                    string.Equals(x.Name, searchTermString, StringComparison.OrdinalIgnoreCase));
                if (result != null)
                {
                    isEnum = false;
                    return result.Key;
                }
            }

            if (searchTerm.ToString()?.StartsWith("@") == true)
            {
                var expression = new OctoExpression(searchTerm.ToString()?.Substring(1));
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
