using System;
using System.Collections.Generic;
using System.Linq;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.Formulas;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

internal class SingleOriginRtQuery : SingleOriginRtQuery<RtEntity>
{
    internal SingleOriginRtQuery(EntityCacheItem entityCacheItem, IDatabaseContext databaseContext, string language)
        : base(entityCacheItem, databaseContext, language)
    {
    }
}

internal class SingleOriginRtQuery<TEntity> : SingleOriginQuery<TEntity> where TEntity : RtEntity, new()
{
    private readonly EntityCacheItem _entityCacheItem;

    internal SingleOriginRtQuery(EntityCacheItem entityCacheItem, IDatabaseContext databaseContext, string language)
        : base(databaseContext.GetRtCollection<TEntity>(entityCacheItem.CkId), language)
    {
        _entityCacheItem = entityCacheItem;
    }

    protected override bool IsAttributeNameValid(string attributeName)
    {
        return _entityCacheItem.Attributes.TryGetValue(attributeName, out var _) ||
               attributeName == nameof(RtEntity.RtId) ||
               attributeName == nameof(RtEntity.RtCreationDateTime) ||
               attributeName == nameof(RtEntity.RtChangedDateTime) ||
               attributeName == nameof(RtEntity.RtWellKnownName) ||
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

    protected override string GetEntityName()
    {
        return _entityCacheItem.CkId;
    }

    protected override object? ResolveSearchAttributeValue(string attributeName, object? searchTerm, out bool isEnum)
    {
        if (searchTerm != null &&
            _entityCacheItem.Attributes.TryGetValue(attributeName, out var attributeCacheItem))
        {
            if (attributeCacheItem.SelectionValues != null)
            {
                var searchTermString = searchTerm.ToString()?.Replace("_", "");

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
            }

            // Change to the type of attribute
            isEnum = false;
            return RtEntity.ConvertAttributeValue(attributeCacheItem.AttributeValueType, searchTerm);
        }

        return base.ResolveSearchAttributeValue(attributeName, searchTerm, out isEnum);
    }

    protected override IEnumerable<GroupingDto>? CalculateGrouping(IEnumerable<TEntity> resultList)
    {
        if (GroupBy == null)
        {
            return null;
        }

        var statisticFunctions = new RtStatisticFunctions<TEntity>(_entityCacheItem, GroupBy);
        return statisticFunctions.Calculate(resultList);
    }

   


}