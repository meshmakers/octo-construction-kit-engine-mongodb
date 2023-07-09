using System;
using System.Collections.Generic;
using System.Linq;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class RtStatisticFunctions<TEntity> where TEntity : RtEntity
{
    private readonly EntityCacheItem _entityCacheItem;

    public RtStatisticFunctions(EntityCacheItem entityCacheItem, FieldGroupBy groupBy)
    {
        _entityCacheItem = entityCacheItem;
        GroupBy = groupBy;
    }

    public FieldGroupBy GroupBy { get; }

    public IEnumerable<GroupingDto> Calculate(IEnumerable<TEntity> resultList)
    {
        var groupByPropertiesResult = resultList.GroupBy(g=> new Key(GroupBy.AttributeNames.Select(a=> GetValue(a)(g))));

        List<GroupingDto> calculateGrouping = new List<GroupingDto>();
        foreach (IGrouping<Key, TEntity> entityGrouping in groupByPropertiesResult)
        {
            var grouping = new GroupingDto
            {
                GroupByAttributeNames = GroupBy.AttributeNames,
                Keys = entityGrouping.Key.Keys,
                Count = entityGrouping.Count(),
                CountStatistics = RunStatistics(GroupBy.CountAttributeNames,
                    attributeName => entityGrouping.Count(x => GetValue(attributeName)(x) != null)),
                MinStatistics = RunStatistics(GroupBy.MinValueAttributeNames,
                    attributeName => entityGrouping.Min(x => GetValue(attributeName)(x))),
                MaxStatistics = RunStatistics(GroupBy.MaxValueAttributeNames,
                    attributeName => entityGrouping.Max(x => GetValue(attributeName)(x))),
                AvgStatistics = RunStatistics(GroupBy.AvgAttributeNames,
                    attributeName => entityGrouping.Average(x => (decimal?)GetValue(attributeName)(x)))
            };
            calculateGrouping.Add(grouping);
        }

        return calculateGrouping;
    }

    private static List<StatisticsDto> RunStatistics(IEnumerable<string>? attributeNames, Func<string, object?> calcFunction)
    {
        List<StatisticsDto> list = new List<StatisticsDto>();
        if (attributeNames != null)
        {
            foreach (var attributeName in attributeNames)
            {
                list.Add(new StatisticsDto
                {
                    AttributeName = attributeName,
                    Value = calcFunction(attributeName)
                });
            }
        }

        return list;
    }

    private Func<TEntity, object?> GetValue(string propertyName) =>
        entity =>
        {
            if (_entityCacheItem.Attributes.TryGetValue(propertyName, out var attributeCacheItem))
            {
                return RtEntity.ConvertAttributeValue(attributeCacheItem.AttributeValueType, entity.Attributes[propertyName]);
            }

            var property = entity.GetType().GetProperty(propertyName);
            if (property == null)
            {
                throw new OperationFailedException(
                    $"Attribute '{propertyName}' does not exist on type '{typeof(TEntity).Name}'");
            }

            return property.GetValue(entity);
        };

    internal class Key
    {
        private readonly IEnumerable<object?> _keys;

        public Key(IEnumerable<object?> keys)
        {
            _keys = keys;
        }
        
        public IEnumerable<object?> Keys => _keys;

        public override bool Equals(object? obj)
        {
            var t=  obj is Key other && _keys.SequenceEqual(other._keys);
            return t;
        }

        public override int GetHashCode()
        {
            return _keys.Aggregate(0, (current, key) => current ^ key?.GetHashCode() ?? 0);
        }
    }   
}