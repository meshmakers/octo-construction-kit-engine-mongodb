using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text;

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

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal abstract class RtFieldFilterResolver<TEntity>(
    ICkCacheService ckCacheService,
    string tenantId,
    CkTypeWithAttributesGraph ckTypeWithAttributesGraph)
    : FieldFilterResolver<TEntity>
    where TEntity : RtTypeWithAttributes, new()
{
    protected readonly ICkCacheService _ckCacheService = ckCacheService;
    protected readonly string _tenantId = tenantId;

    internal override bool IsAttributePathValid(string attributePath)
    {
        var pathTerms = RtPathEvaluator.TokenizePath(attributePath);

        CkTypeWithAttributesGraph current = ckTypeWithAttributesGraph;
        CkTypeAttributeGraph? ckTypeAttributeGraph = null;
        foreach (var pathTerm in pathTerms)
        {
            switch (pathTerm.Type)
            {
                case PathType.Attribute:
                    if (current.AllAttributesByName.TryGetValue(pathTerm.Value.ToPascalCase(),
                            out ckTypeAttributeGraph))
                    {
                        switch (ckTypeAttributeGraph.ValueType)
                        {
                            case AttributeValueTypesDto.Record:
                            case AttributeValueTypesDto.RecordArray:
                                if (ckTypeAttributeGraph.ValueCkRecordId == null)
                                {
                                    throw OperationFailedException.CkRecordIdNotDefined(ckTypeAttributeGraph);
                                }

                                current = _ckCacheService.GetCkRecord(_tenantId, ckTypeAttributeGraph.ValueCkRecordId);
                                continue;
                            default:
                                continue;
                        }
                    }

                    return false;
                case PathType.ArrayIndex:
                    if (ckTypeAttributeGraph != null)
                    {
                        switch (ckTypeAttributeGraph.ValueType)
                        {
                            case AttributeValueTypesDto.StringArray:
                            case AttributeValueTypesDto.IntArray:
                            case AttributeValueTypesDto.RecordArray:
                                continue;
                            default:
                                return false;
                        }
                    }

                    return false;
                default:
                    throw OperationFailedException.PathTypeNotSupported(pathTerm);
            }
        }

        return true;
    }

    internal override string? ResolveAttributePath(string attributePath)
    {
        var pathTerms = RtPathEvaluator.TokenizePath(attributePath);

        StringBuilder sb = new();
        sb.Append(Constants.AttributesName);

        CkTypeWithAttributesGraph current = ckTypeWithAttributesGraph;
        CkTypeAttributeGraph? ckTypeAttributeGraph = null;
        foreach (var pathTerm in pathTerms)
        {
            switch (pathTerm.Type)
            {
                case PathType.Attribute:

                    if (current.AllAttributesByName.TryGetValue(pathTerm.Value.ToPascalCase(),
                            out ckTypeAttributeGraph))
                    {
                        switch (ckTypeAttributeGraph.ValueType)
                        {
                            case AttributeValueTypesDto.Record:
                            case AttributeValueTypesDto.RecordArray:
                                if (ckTypeAttributeGraph.ValueCkRecordId == null)
                                {
                                    throw OperationFailedException.CkRecordIdNotDefined(ckTypeAttributeGraph);
                                }

                                current = _ckCacheService.GetCkRecord(_tenantId, ckTypeAttributeGraph.ValueCkRecordId);
                                sb.Append(Constants.PathSeparator);
                                sb.Append(pathTerm.Value.ToCamelCase());
                                continue;
                            default:
                                if (current is CkRecordGraph)
                                {
                                    sb.Append(Constants.PathSeparator);
                                    sb.Append(Constants.AttributesName);
                                }

                                sb.Append(Constants.PathSeparator);
                                sb.Append(pathTerm.Value.ToCamelCase());
                                continue;
                        }
                    }

                    return null;
                case PathType.ArrayIndex:
                    if (ckTypeAttributeGraph != null)
                    {
                        switch (ckTypeAttributeGraph.ValueType)
                        {
                            case AttributeValueTypesDto.StringArray:
                            case AttributeValueTypesDto.IntArray:
                            case AttributeValueTypesDto.RecordArray:
                                if (pathTerm.Value != "*")
                                {
                                    sb.Append(string.Format(Constants.IndexAccessor, pathTerm.Value));
                                }

                                continue;
                            default:
                                return null;
                        }
                    }

                    return null;
                default:
                    throw OperationFailedException.PathTypeNotSupported(pathTerm);
            }
        }

        return sb.ToString();
    }

    internal override object? ResolveSearchAttributeValue(string attributePath, object? searchTerm, out bool isEnum)
    {
        // Search for the correct attribute in the CkTypeAttributesGraph
        var pathTerms = RtPathEvaluator.TokenizePath(attributePath);
        if (searchTerm != null)
        {
            CkTypeAttributeGraph? currentTypeAttributeGraph = null;
            var currentCkTypeWithAttributesGraph = ckTypeWithAttributesGraph;
            foreach (var pathTerm in pathTerms)
            {
                switch (pathTerm.Type)
                {
                    // Currently, we only support attributes. Further path types need to be implemented
                    case PathType.Attribute:
                        if (currentCkTypeWithAttributesGraph.AllAttributesByName.TryGetValue(
                                pathTerm.Value.ToPascalCase(), out var ckTypeAttributeGraph))
                        {
                            currentTypeAttributeGraph = ckTypeAttributeGraph;

                            if (ckTypeAttributeGraph.ValueType == AttributeValueTypesDto.Record ||
                                ckTypeAttributeGraph.ValueType == AttributeValueTypesDto.RecordArray)
                            {
                                if (ckTypeAttributeGraph.ValueCkRecordId == null)
                                {
                                    throw OperationFailedException.CkRecordIdNotDefined(ckTypeAttributeGraph);
                                }

                                currentCkTypeWithAttributesGraph =
                                    _ckCacheService.GetCkRecord(_tenantId, ckTypeAttributeGraph.ValueCkRecordId);
                            }
                        }

                        break;
                    default:
                        throw OperationFailedException.PathTypeNotSupported(pathTerm);
                }
            }

            if (currentTypeAttributeGraph == null)
            {
                throw OperationFailedException.CkTypeAttributePathNotFound(ckTypeWithAttributesGraph, attributePath);
            }

            if (currentTypeAttributeGraph.ValueType == AttributeValueTypesDto.Enum &&
                currentTypeAttributeGraph.ValueCkEnumId != null)
            {
                var ckEnumGraph = _ckCacheService.GetCkEnum(_tenantId, currentTypeAttributeGraph.ValueCkEnumId);

                if (searchTerm is string str && str.StartsWith("[") && str.EndsWith("]"))
                {
                    // Handle enum array search term
                    var enumKeys = new List<int>();
                    var enumValues = str.Trim('[', ']').Split(',')
                        .Select(x => x.Trim())
                        .Where(x => !string.IsNullOrWhiteSpace(x));

                    foreach (var value in enumValues)
                    {
                        if (TryGetEnumKey(value, ckEnumGraph, out var enumKey))
                        {
                            enumKeys.Add(enumKey.Value);
                        }
                    }

                    isEnum = false;
                    return enumKeys;
                }

                if (searchTerm is IEnumerable termList)
                {
                    List<int> enumKeys = new();
                    foreach (var v in termList)
                    {
                        if (TryGetEnumKey(v, ckEnumGraph, out var enumKey))
                        {
                            enumKeys.Add(enumKey.Value);
                        }
                    }

                    isEnum = false;
                    return enumKeys;
                }

                if (TryGetEnumKey(searchTerm, ckEnumGraph, out var enumKeySingle))
                {
                    isEnum = false;
                    return enumKeySingle;
                }
            }

            if (currentTypeAttributeGraph.ValueType == AttributeValueTypesDto.RecordArray &&
                currentTypeAttributeGraph.ValueCkRecordId != null)
            {
                if (searchTerm is FieldFilterCriteria fieldFilterCriteria)
                {
                    var ckRecordGraph =
                        _ckCacheService.GetCkRecord(_tenantId, currentTypeAttributeGraph.ValueCkRecordId);
                    var rtRecordFieldFilterResolver =
                        new RtRecordFieldFilterResolver<RtRecord>(_ckCacheService, _tenantId, ckRecordGraph);
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
                        switch (currentTypeAttributeGraph.ValueType)
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

            if (searchTerm is IEnumerable e and not string)
            {
                isEnum = false;
                return e;
            }

            // Change to the type of attribute
            isEnum = false;
            return AttributeValueConverter.ConvertAttributeValue(currentTypeAttributeGraph.ValueType, searchTerm);
        }

        return base.ResolveSearchAttributeValue(attributePath, searchTerm, out isEnum);
    }

    private static bool TryGetEnumKey(object searchTerm, CkEnumGraph ckEnumGraph, [NotNullWhen(true)] out int? key)
    {
        if (int.TryParse(searchTerm.ToString(), out var searchTermInt))
        {
            // Search for match in selection value
            var enumValueDto = ckEnumGraph.Values.FirstOrDefault(x => x.Key == searchTermInt);
            if (enumValueDto != null)
            {
                key = enumValueDto.Key;
                return true;
            }
        }

        var searchTermString = searchTerm.ToString()?.Replace("_", "");

        // Search for match in selection value
        var result = ckEnumGraph.Values.FirstOrDefault(x =>
            string.Equals(x.Name, searchTermString, StringComparison.OrdinalIgnoreCase));
        if (result != null)
        {
            key = result.Key;
            return true;
        }

        key = null;
        return false;
    }
}
