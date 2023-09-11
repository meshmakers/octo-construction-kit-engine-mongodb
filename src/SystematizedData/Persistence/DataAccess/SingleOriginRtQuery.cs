using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.Formulas;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

internal class SingleOriginRtQuery : SingleOriginRtQuery<RtEntity>
{
    internal SingleOriginRtQuery(ICkCacheService ckCacheService, string tenantId, CkTypeGraph entityCacheItem, IDatabaseContext databaseContext, string language)
        : base(ckCacheService, tenantId, entityCacheItem, databaseContext, language)
    {
    }
}

internal class SingleOriginRtQuery<TEntity> : SingleOriginQuery<TEntity> where TEntity : RtEntity, new()
{
    private readonly ICkCacheService _ckCacheService;
    private readonly string _tenantId;
    private readonly CkTypeGraph _entityCacheItem;

    internal SingleOriginRtQuery(ICkCacheService ckCacheService, string tenantId, CkTypeGraph entityCacheItem, IDatabaseContext databaseContext, string language)
        : base(databaseContext.GetRtCollection<TEntity>(entityCacheItem.CkTypeId), language)
    {
        _ckCacheService = ckCacheService;
        _tenantId = tenantId;
        _entityCacheItem = entityCacheItem;
    }

    protected override bool IsAttributeNameValid(string attributeName)
    {
        return _entityCacheItem.AllAttributes.TryGetValue(attributeName, out var _) ||
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

    protected override string GetEntityName()
    {
        return _entityCacheItem.CkTypeId.FullName;
    }

    protected override object? ResolveSearchAttributeValue(string attributeName, object? searchTerm, out bool isEnum)
    {
        if (searchTerm != null &&
            _entityCacheItem.AllAttributes.TryGetValue(attributeName, out var attributeCacheItem))
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
                        throw new OperationFailedException($"Term '{searchTerm}' cannot be evaluated by formula.");
                    }
                }
            }

            // Change to the type of attribute
            isEnum = false;
            return RtEntity.ConvertAttributeValue(attributeCacheItem.ValueType, searchTerm);
        }

        return base.ResolveSearchAttributeValue(attributeName, searchTerm, out isEnum);
    }
}
