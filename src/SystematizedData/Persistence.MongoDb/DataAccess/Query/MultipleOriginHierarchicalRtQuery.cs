using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.Formulas;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

internal class MultipleOriginHierarchicalRtQuery : MultipleOriginHierarchicalRtQuery<RtEntity>
{
    internal MultipleOriginHierarchicalRtQuery(ICkCacheService ckCacheService, string tenantId, CkTypeGraph targetEntityCacheItem,
        IDatabaseContext databaseContext,
        string language, IEnumerable<OctoObjectId> rtIds, CkId<CkTypeId> originCkTypeId, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, CkId<CkTypeId> targetCkTypeId)
        : base(ckCacheService, tenantId, targetEntityCacheItem, databaseContext, language, rtIds, originCkTypeId, roleId, graphDirection,
            targetCkTypeId)
    {
    }
}

internal class MultipleOriginHierarchicalRtQuery<TTargetEntity> : Query<TTargetEntity> where TTargetEntity : RtEntity, new()
{
    private readonly IDatabaseContext _databaseContext;
    private readonly GraphDirections _graphDirection;
    private readonly CkId<CkTypeId> _originCkTypeId;
    private readonly CkId<CkAssociationRoleId> _roleId;
    private readonly IEnumerable<OctoObjectId> _rtIds;
    private readonly CkId<CkTypeId> _targetCkTypeId;
    private readonly ICkCacheService _ckCacheService;
    private readonly string _tenantId;
    private readonly CkTypeGraph _targetEntityCacheItem;

    internal MultipleOriginHierarchicalRtQuery(ICkCacheService ckCacheService, string tenantId, CkTypeGraph targetEntityCacheItem,
        IDatabaseContext databaseContext,
        string language, IEnumerable<OctoObjectId> rtIds, CkId<CkTypeId> originCkTypeId, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, CkId<CkTypeId> targetCkTypeId)
        : base(language)
    {
        _ckCacheService = ckCacheService;
        _tenantId = tenantId;
        _targetEntityCacheItem = targetEntityCacheItem;
        _databaseContext = databaseContext;
        _rtIds = rtIds;
        _originCkTypeId = originCkTypeId;
        _roleId = roleId;
        _graphDirection = graphDirection;
        _targetCkTypeId = targetCkTypeId;
    }

    internal async Task<IMultipleOriginResultSet<TTargetEntity>> ExecuteQuery(IOctoSession session, int? skip,
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
                _databaseContext.GetRtDatabaseCollection<TTargetEntity>(_targetCkTypeId).GetMongoCollection(),
                innerLocalField,
                "_id",
                (FieldDefinition<BsonDocument>)"target"),
            PipelineStageDefinitionBuilder.Unwind((FieldDefinition<BsonDocument>)"target"),
            PipelineStageDefinitionBuilder.ReplaceRoot<BsonDocument, TTargetEntity>("$target")
        });


        AddTextFilterConstraintsToPipeline(pipelineStageDefinitions);
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions != null)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(filterDefinitions));
        }

        AddSortConstraintsToPipeline(pipelineStageDefinitions);


        var aggregate = _databaseContext.GetRtDatabaseCollection<RtEntity>(_originCkTypeId).Aggregate(session)
            .Match(
                Builders<RtEntity>.Filter.And(Builders<RtEntity>.Filter.In(x => x.RtId, _rtIds)))
            .Lookup(
                _databaseContext.RtDatabaseAssociations.GetMongoCollection(),
                connectFromField,
                connectToField,
                PipelineDefinition<RtAssociation, TTargetEntity>.Create(pipelineStageDefinitions),
                @as
            );

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
        return _targetEntityCacheItem.AllAttributes.TryGetValue(attributeName, out var _) ||
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
            _targetEntityCacheItem.AllAttributes.TryGetValue(attributeName, out var attributeCacheItem))
        {
            if (attributeCacheItem.ValueType == AttributeValueTypesDto.Enum && attributeCacheItem.ValueCkEnumId != null)
            {
                var ckEnumGraph = _ckCacheService.GetCkEnum(_tenantId, attributeCacheItem.ValueCkEnumId.Value);
                var searchTermString = searchTerm.ToString()?.Replace("_", "");

                // Search for match in selection value
                var result = ckEnumGraph.Values.FirstOrDefault(x =>
                    string.Equals(x.Name, searchTermString, StringComparison.OrdinalIgnoreCase));
                if (result != null)
                {
                    isEnum = false;
                    return result.Key;
                }
            }

            if (searchTerm.ToString()?.StartsWith("@") == true)
            {
                var expressionString = searchTerm.ToString()?.Substring(1);
                if (string.IsNullOrWhiteSpace(expressionString) && expressionString != null)
                {
                    var expression = new OctoExpression(expressionString);
                    var result = expression.calculate();
                    
                    if (double.IsNegativeInfinity(result))
                    {
                        isEnum = false;
                        return null;
                    }

                    if (!double.IsNaN(result))
                    {
                        switch (attributeCacheItem.ValueType)
                        {
                            case AttributeValueTypesDto.DateTime:
                                isEnum = false;
                                return new DateTime((long)result);
                        }
                    }
                    else
                    {
                        throw OperationFailedException.FormulaCalculationFailed(searchTerm);
                    }
                }
                else
                {
                    throw OperationFailedException.FormulaEvaluationFailed(searchTerm);
                }
            }

            // Change to the type of attribute
            isEnum = false;
            return AttributeValueConverter.ConvertAttributeValue(attributeCacheItem.ValueType, searchTerm);
        }

        return base.ResolveSearchAttributeValue(attributeName, searchTerm, out isEnum);
    }
}