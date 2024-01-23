using System.Collections;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Formulas;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal abstract class RtFieldFilterResolver<TEntity> : FieldFilterResolver<TEntity>
    where TEntity : RtTypeWithAttributes, new()
{
    private readonly ICkCacheService _ckCacheService;
    private readonly string _tenantId;
    private readonly CkTypeWithAttributesGraph _ckTypeWithAttributesGraph;

    public RtFieldFilterResolver(ICkCacheService ckCacheService, string tenantId, CkTypeWithAttributesGraph ckTypeWithAttributesGraph)
    {
        _ckCacheService = ckCacheService;
        _tenantId = tenantId;
        _ckTypeWithAttributesGraph = ckTypeWithAttributesGraph;
    }
    
    internal override bool IsAttributeNameValid(string attributeName)
    {
        return _ckTypeWithAttributesGraph.AllAttributesByName.ContainsKey(attributeName);
    }

    internal override string ResolveAttributeName(string attributeName)
    {
        var baseResolve = base.ResolveAttributeName(attributeName);
        if (!string.IsNullOrEmpty(baseResolve))
        {
            return baseResolve;
        }

        return $"{Constants.AttributesName}.{attributeName.ToCamelCase()}";
    }

    internal override object? ResolveSearchAttributeValue(string attributeName, object? searchTerm, out bool isEnum)
    {
        if (searchTerm != null &&
            _ckTypeWithAttributesGraph.AllAttributesByName.TryGetValue(attributeName, out var ckTypeAttributeGraph))
        {
            if (ckTypeAttributeGraph.ValueType == AttributeValueTypesDto.Enum && ckTypeAttributeGraph.ValueCkEnumId != null)
            {
                var ckEnumGraph = _ckCacheService.GetCkEnum(_tenantId, ckTypeAttributeGraph.ValueCkEnumId.Value);
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

            if (ckTypeAttributeGraph.ValueType == AttributeValueTypesDto.RecordArray && ckTypeAttributeGraph.ValueCkRecordId != null)
            {
                if (searchTerm is FieldFilterCriteria fieldFilterCriteria)
                {
                    var ckRecordGraph = _ckCacheService.GetCkRecord(_tenantId, ckTypeAttributeGraph.ValueCkRecordId.Value);
                    var rtRecordFieldFilterResolver = new RtRecordFieldFilterResolver<RtRecord>(_ckCacheService, _tenantId, ckRecordGraph);
                    rtRecordFieldFilterResolver.AddFieldFilters(fieldFilterCriteria.FieldFilters);

                    if (rtRecordFieldFilterResolver.FilterDefinitions.Any())
                    {
                        if (rtRecordFieldFilterResolver.FilterDefinitions.Count == 1)
                        {
                            isEnum = false;
                            return rtRecordFieldFilterResolver.FilterDefinitions.First();
                        }

                        isEnum = false;
                        return Builders<RtRecord>.Filter.And(rtRecordFieldFilterResolver.FilterDefinitions.ToArray());
                    }
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
                        switch (ckTypeAttributeGraph.ValueType)
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
            return AttributeValueConverter.ConvertAttributeValue(ckTypeAttributeGraph.ValueType, searchTerm);
        }

        return base.ResolveSearchAttributeValue(attributeName, searchTerm, out isEnum);
    }
}