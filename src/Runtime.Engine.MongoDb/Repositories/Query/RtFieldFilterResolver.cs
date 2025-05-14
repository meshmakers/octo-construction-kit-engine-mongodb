using System.Collections;
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
                    if (current.AllAttributesByName.TryGetValue(pathTerm.Value.ToPascalCase(), out ckTypeAttributeGraph))
                    {
                        switch (ckTypeAttributeGraph.ValueType)
                        {
                            case AttributeValueTypesDto.Record:
                            case AttributeValueTypesDto.RecordArray:
                                if (ckTypeAttributeGraph.ValueCkRecordId == null)
                                {
                                    throw OperationFailedException.CkRecordIdNotDefined(ckTypeAttributeGraph);
                                }

                                current = ckCacheService.GetCkRecord(tenantId, ckTypeAttributeGraph.ValueCkRecordId);
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

                    if (current.AllAttributesByName.TryGetValue(pathTerm.Value.ToPascalCase(), out ckTypeAttributeGraph))
                    {
                        switch (ckTypeAttributeGraph.ValueType)
                        {
                            case AttributeValueTypesDto.Record:
                            case AttributeValueTypesDto.RecordArray:
                                if (ckTypeAttributeGraph.ValueCkRecordId == null)
                                {
                                    throw OperationFailedException.CkRecordIdNotDefined(ckTypeAttributeGraph);
                                }

                                current = ckCacheService.GetCkRecord(tenantId, ckTypeAttributeGraph.ValueCkRecordId);
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

    internal override object? ResolveSearchAttributeValue(string attributeName, object? searchTerm, out bool isEnum)
    {
        if (searchTerm != null &&
            ckTypeWithAttributesGraph.AllAttributesByName.TryGetValue(attributeName, out var ckTypeAttributeGraph))
        {
            if (ckTypeAttributeGraph.ValueType == AttributeValueTypesDto.Enum &&
                ckTypeAttributeGraph.ValueCkEnumId != null)
            {
                var ckEnumGraph = ckCacheService.GetCkEnum(tenantId, ckTypeAttributeGraph.ValueCkEnumId);
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

            if (ckTypeAttributeGraph.ValueType == AttributeValueTypesDto.RecordArray &&
                ckTypeAttributeGraph.ValueCkRecordId != null)
            {
                if (searchTerm is FieldFilterCriteria fieldFilterCriteria)
                {
                    var ckRecordGraph = ckCacheService.GetCkRecord(tenantId, ckTypeAttributeGraph.ValueCkRecordId);
                    var rtRecordFieldFilterResolver =
                        new RtRecordFieldFilterResolver<RtRecord>(ckCacheService, tenantId, ckRecordGraph);
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

            if (searchTerm is IEnumerable e)
            {
                isEnum = false;
                return e;
            }

            // Change to the type of attribute
            isEnum = false;
            return AttributeValueConverter.ConvertAttributeValue(ckTypeAttributeGraph.ValueType, searchTerm);
        }

        return base.ResolveSearchAttributeValue(attributeName, searchTerm, out isEnum);
    }
}
