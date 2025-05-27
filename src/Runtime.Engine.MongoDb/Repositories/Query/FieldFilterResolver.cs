using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Formulas;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class FieldFilterResolver<TEntity>
{
    private readonly List<FilterDefinition<TEntity>> _fieldFilters = new();
    private readonly BsonClassMap _bsonClassMap = BsonClassMap.LookupClassMap(typeof(TEntity));

    public IReadOnlyList<FilterDefinition<TEntity>> FilterDefinitions => _fieldFilters;
    
    internal virtual string GetEntityName()
    {
        return typeof(TEntity).Name;
    }
    
    internal virtual bool IsAttributePathValid(string attributePath)
    {
        var memberMap = _bsonClassMap.GetMemberMap(attributePath);
        return memberMap != null;
    }

    internal virtual string? ResolveAttributePath(string attributePath)
    {
        if (_bsonClassMap.IdMemberMap?.MemberName == attributePath)
        {
            return Constants.IdField;
        }

        var memberMap = _bsonClassMap.GetMemberMap(attributePath);
        if (memberMap == null || (!memberMap.ShouldSerializeMethod?.Invoke(null) ?? false))
        {
            return null;
        }

        return memberMap.ElementName;
    }
    
    internal virtual object? ResolveSearchAttributeValue(string attributePath, object? searchTerm, out bool isEnum)
    {
        if (searchTerm == null)
        {
            isEnum = false;
            return null;
        }

        var propertyType = typeof(TEntity).GetProperty(attributePath)?.PropertyType;
        if (propertyType != null && propertyType.IsEnum)
        {
            var nameCandidates = Enum.GetNames(propertyType)
                .Where(x => x.ToLower().Contains(searchTerm.ToString()?.ToLower() ?? string.Empty));

            var values = new List<object>();
            foreach (var nameCandidate in nameCandidates)
            {
                values.Add(Enum.Parse(propertyType, nameCandidate));
            }

            isEnum = true;
            return values.ToArray();
        }

        if (searchTerm.ToString()!.StartsWith("@"))
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
                    if (propertyType == typeof(DateTime) || propertyType == typeof(DateTime?))
                    {
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

        if (propertyType != null && searchTerm is string term)
        {
            isEnum = false;

            if (propertyType == typeof(ObjectId))
            {
                return ObjectId.Parse(term);
            }

            if (propertyType == typeof(DateTime) || propertyType == typeof(DateTime?))
            {
                return DateTime.Parse(term);
            }

            try
            {
                return Convert.ChangeType(term, propertyType);
            }
            catch (Exception)
            {
                // Indented to not handle exception
            }
        }

        isEnum = false;
        return searchTerm;
    }

    internal void AddFieldFilters(IEnumerable<FieldFilter>? fieldFilters)
    {
        if (fieldFilters == null)
        {
            return;
        }

        foreach (var fieldFilter in fieldFilters)
        {
            AddFieldFilter(fieldFilter);
        }
    }

    private void AddFieldFilter(FieldFilter fieldFilter)
    {
        if (string.IsNullOrWhiteSpace(fieldFilter.AttributePath))
        {
            return;
        }

        if (IsAttributePathValid(fieldFilter.AttributePath))
        {
            var resolvedAttributeName = ResolveAttributePath(fieldFilter.AttributePath);
            var resolvedValue = ResolveSearchAttributeValue(fieldFilter.AttributePath, fieldFilter.ComparisonValue,
                out var isEnum);

            if (isEnum)
            {
                _fieldFilters.Add(Builders<TEntity>.Filter.AnyIn(resolvedAttributeName,
                    resolvedValue != null ? (IEnumerable<object>)resolvedValue : []));
            }
            else if (!string.IsNullOrWhiteSpace(resolvedAttributeName))
            {
                var filter = CreateScalarFilter(resolvedAttributeName, fieldFilter.Operator, resolvedValue);
                _fieldFilters.Add(filter);
            }
            else
            {
                throw OperationFailedException.AttributeNameResolutionFailed(fieldFilter.AttributePath);
            }
        }
        else
        {
            throw OperationFailedException.AttributeDoesNotExist(fieldFilter.AttributePath, GetEntityName());
        }
    }
    
    internal FilterDefinition<TEntity> CreateScalarFilter(string attributeName, FieldFilterOperator comparisonOperator,
        object? value)
    {
        switch (comparisonOperator)
        {
            case FieldFilterOperator.Equals:
                return Builders<TEntity>.Filter.Eq(attributeName, value);
            case FieldFilterOperator.NotEquals:
                return Builders<TEntity>.Filter.Ne(attributeName, value);
            case FieldFilterOperator.In:
                object[] array = ComparisonValueToArray(value);
                return Builders<TEntity>.Filter.In(attributeName, array);
            case FieldFilterOperator.NotIn:
                array = ComparisonValueToArray(value);
                return Builders<TEntity>.Filter.Nin(attributeName, array);
            case FieldFilterOperator.LessThan:
                return Builders<TEntity>.Filter.Lt(attributeName, value);
            case FieldFilterOperator.LessEqualThan:
                return Builders<TEntity>.Filter.Lte(attributeName, value);
            case FieldFilterOperator.GreaterThan:
                return Builders<TEntity>.Filter.Gt(attributeName, value);
            case FieldFilterOperator.GreaterEqualThan:
                return Builders<TEntity>.Filter.Gte(attributeName, value);
            case FieldFilterOperator.Like:
                return Builders<TEntity>.Filter.Regex(attributeName,
                    new BsonRegularExpression(GetRegex(value?.ToString()), "i"));
            case FieldFilterOperator.MatchRegEx:
                return Builders<TEntity>.Filter.Regex(attributeName,
                    new BsonRegularExpression(value?.ToString()));
            case FieldFilterOperator.AnyEq:
                return Builders<TEntity>.Filter.AnyEq(attributeName, value);
            case FieldFilterOperator.AnyLike:
                return Builders<TEntity>.Filter.AnyEq(attributeName, new BsonRegularExpression(GetRegex(value?.ToString()), "i"));
            case FieldFilterOperator.Match:
                if (value is FilterDefinition<RtRecord> filterMatch)
                {
                    return Builders<TEntity>.Filter.ElemMatch(attributeName, filterMatch);
                }
                throw OperationFailedException.MatchFilterValueNotSupported(value);
            default:
                throw OperationFailedException.OperatorNotSupported(comparisonOperator);
        }
    }

    private static object[] ComparisonValueToArray(object? value)
    {
        var array = new object[]{};
        if (value is string stringValue)
        {
            if (stringValue.Contains(","))
            {
                array = stringValue.Split(',').ToArray<object>() ?? [];
            }
        }
        else if (value is IEnumerable<object> enumerable)
        {
            array = enumerable.ToArray();
        }

        return array;
    }

    private static string? GetRegex(string? value)
    {
        return value?.Replace("*", ".*");
    }
}
