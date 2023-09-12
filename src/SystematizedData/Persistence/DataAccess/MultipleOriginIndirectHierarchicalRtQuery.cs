using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.Ck;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.Formulas;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

// ReSharper disable once UnusedType.Global
internal class MultipleOriginIndirectHierarchicalRtQuery : MultipleOriginIndirectHierarchicalRtQuery<RtEntity>
{
    internal MultipleOriginIndirectHierarchicalRtQuery(ICkCacheService ckCacheService, string tenantId, CkTypeGraph targetEntityCacheItem,
        IDatabaseContext databaseContext,
        string language, IEnumerable<OctoObjectId> rtIds, CkId<CkTypeId> originCkTypeId, CkId<CkAssociationRoleId> roleId,
        GraphDirections graphDirection, CkId<CkTypeId> targetCkTypeId)
        : base(ckCacheService, tenantId, targetEntityCacheItem, databaseContext, language, rtIds, originCkTypeId, roleId, graphDirection,
            targetCkTypeId)
    {
    }
}

internal class MultipleOriginIndirectHierarchicalRtQuery<TTargetEntity> : Query<TTargetEntity> where TTargetEntity : RtEntity, new()
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

    internal MultipleOriginIndirectHierarchicalRtQuery(ICkCacheService ckCacheService, string tenantId, CkTypeGraph targetEntityCacheItem,
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

    internal async Task<MultipleOriginResultSet<TTargetEntity>> ExecuteQuery(IOctoSession session, int? skip,
        int? take)
    {
        if (skip.HasValue && !take.HasValue)
        {
            throw new PersistenceException("'skip' without 'take' is not possible.");
        }

        var connectFromRtIdField = "targetRtId";
        var connectToRtIdField = "originRtId";
        var connectToCkTypeIdField = "originCkTypeId";
        var @as = "_associations";

        switch (_graphDirection)
        {
            case GraphDirections.Inbound:
                connectFromRtIdField = "originRtId";
                connectToRtIdField = "originCkTypeId";
                connectToCkTypeIdField = "targetCkTypeId";
                break;
            case GraphDirections.Outbound:
                break;
            default:
                throw new PersistenceException($"'{_graphDirection}' is not supported.");
        }


        var pipelineStageDefinitions = new List<IPipelineStageDefinition>();

        var associationFilter = new FilterDefinitionBuilder<RtAssociation>().Eq(x => x.AssociationRoleId, _roleId);

        var startWith =
            new ExpressionAggregateExpressionDefinition<RtEntity, BsonValue>(x => x.RtId.ToObjectId(), new ExpressionTranslationOptions());

        AddTextFilterConstraintsToPipeline(pipelineStageDefinitions);
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions != null)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(filterDefinitions));
        }

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
                                                "$$this." + connectToCkTypeIdField,
                                                _targetCkTypeId.FullName
                                            })
                                    }
                                })
                        },
                        {
                            "sortBy",
                            new BsonDocument("depth", 1)
                        }
                    }));

        var aggregate = _databaseContext.GetRtCollection<RtEntity>(_originCkTypeId).Aggregate(session)
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
                foreignCollection: _databaseContext.GetRtCollection<TTargetEntity>(_targetCkTypeId).GetMongoCollection(),
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
            var searchTermString = searchTerm.ToString();

            if (string.IsNullOrWhiteSpace(searchTermString))
            {
                isEnum = false;
                return null;
            }

            if (attributeCacheItem.ValueType == AttributeValueTypesDto.Enum && attributeCacheItem.ValueCkEnumId != null)
            {
                var ckEnumGraph = _ckCacheService.GetCkEnum(_tenantId, attributeCacheItem.ValueCkEnumId.Value);
                var searchTermStringEnum = searchTermString.Replace("_", "");

                // Search for match in selection value
                var result = ckEnumGraph.Values.FirstOrDefault(x =>
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
                    switch (attributeCacheItem.ValueType)
                    {
                        case AttributeValueTypesDto.DateTime:
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
            return RtEntity.ConvertAttributeValue(attributeCacheItem.ValueType, searchTerm);
        }

        return base.ResolveSearchAttributeValue(attributeName, searchTerm, out isEnum);
    }
}