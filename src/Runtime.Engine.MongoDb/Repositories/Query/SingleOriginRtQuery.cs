using System.Collections;
using Meshmakers.Common.Metrics.Context;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Formulas;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class SingleOriginRtQuery<TEntity> : SingleOriginQuery<OctoObjectId, TEntity> where TEntity : RtEntity, new()
{
    private readonly ICkCacheService _ckCacheService;
    private readonly CkTypeGraph _ckTypeGraph;
    private readonly string _tenantId;

    internal SingleOriginRtQuery(IMetricsContext metricsContext, ICkCacheService ckCacheService, string tenantId,
        CkTypeGraph ckTypeGraph,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource, string language)
        : base(metricsContext, mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(ckTypeGraph.CkTypeId), language)
    {
        _ckCacheService = ckCacheService;
        _tenantId = tenantId;
        _ckTypeGraph = ckTypeGraph;
    }

    protected override bool IsAttributeNameValid(string attributeName)
    {
        return _ckTypeGraph.AllAttributesByName.ContainsKey(attributeName) ||
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

    protected override void AddPreFieldFilters(List<FilterDefinition<TEntity>> filters)
    {
        base.AddPreFieldFilters(filters);
        
        // Add filter for ck type and derived ones
        var ckTypeIds =_ckTypeGraph.GetAllDerivedTypes(true);
        filters.Add(Builders<TEntity>.Filter.In(f=> f.CkTypeId, ckTypeIds));
    }

    protected override string GetEntityName()
    {
        return _ckTypeGraph.CkTypeId.FullName;
    }

    protected override object? ResolveSearchAttributeValue(string attributeName, object? searchTerm, out bool isEnum)
    {
        if (searchTerm != null &&
            _ckTypeGraph.AllAttributesByName.TryGetValue(attributeName, out var attributeCacheItem))
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

            if (searchTerm is IEnumerable e)
            {
                isEnum = false;
                return e;
            }

            if (searchTerm.ToString()?.StartsWith("@") == true)
            {
                var expressionString = searchTerm.ToString()?.Substring(1);
                if (!string.IsNullOrWhiteSpace(expressionString))
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
                        throw OperationFailedException.FormulaEvaluationFailed(searchTerm);
                    }
                }
            }

            // Change to the type of attribute
            isEnum = false;
            return AttributeValueConverter.ConvertAttributeValue(attributeCacheItem.ValueType, searchTerm);
        }

        return base.ResolveSearchAttributeValue(attributeName, searchTerm, out isEnum);
    }

    protected override IEnumerable<GroupingResult>? CalculateGrouping(IEnumerable<TEntity> resultList)
    {
        if (GroupBy == null)
        {
            return null;
        }

        var statisticFunctions = new RtStatisticFunctions<TEntity>(_ckTypeGraph, GroupBy);
        return statisticFunctions.Calculate(resultList);
    }
}