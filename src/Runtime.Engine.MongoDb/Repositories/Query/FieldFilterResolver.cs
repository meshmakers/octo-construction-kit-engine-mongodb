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
    private readonly List<FilterDefinition<TEntity>> _fieldFilters;
    private readonly BsonClassMap<TEntity> _bsonClassMap;

    public FieldFilterResolver()
    {
        _fieldFilters = new List<FilterDefinition<TEntity>>();
        
        _bsonClassMap = new BsonClassMap<TEntity>();
        _bsonClassMap.AutoMap();
    }

    public IReadOnlyList<FilterDefinition<TEntity>> FilterDefinitions => _fieldFilters;
    
    internal virtual string GetEntityName()
    {
        return typeof(TEntity).Name;
    }
    
    internal virtual bool IsAttributeNameValid(string attributeName)
    {
        var memberMap = _bsonClassMap.GetMemberMap(attributeName);
        return memberMap != null;
    }

    internal virtual string? ResolveAttributeName(string attributeName)
    {
        if (_bsonClassMap.IdMemberMap?.MemberName == attributeName)
        {
            return Constants.IdField;
        }

        var memberMap = _bsonClassMap.GetMemberMap(attributeName);
        if (memberMap == null || !memberMap.ShouldSerializeMethod.Invoke(null))
        {
            return null;
        }

        return memberMap.ElementName;
    }
    
    internal virtual object? ResolveSearchAttributeValue(string attributeName, object? searchTerm, out bool isEnum)
    {
        if (searchTerm == null)
        {
            isEnum = false;
            return null;
        }

        var propertyType = typeof(TEntity).GetProperty(attributeName)?.PropertyType;
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
        if (string.IsNullOrWhiteSpace(fieldFilter.AttributeName))
        {
            return;
        }

        if (IsAttributeNameValid(fieldFilter.AttributeName))
        {
            var resolvedAttributeName = ResolveAttributeName(fieldFilter.AttributeName);
            var resolvedValue = ResolveSearchAttributeValue(fieldFilter.AttributeName, fieldFilter.ComparisonValue,
                out var isEnum);

            if (isEnum)
            {
                _fieldFilters.Add(Builders<TEntity>.Filter.AnyIn(resolvedAttributeName,
                    resolvedValue != null ? (IEnumerable<object>)resolvedValue : Array.Empty<object>()));
            }
            else if (!string.IsNullOrWhiteSpace(resolvedAttributeName))
            {
                var filter = CreateScalarFilter(resolvedAttributeName, fieldFilter.Operator, resolvedValue);
                _fieldFilters.Add(filter);
            }
            else
            {
                throw OperationFailedException.AttributeNameResolutionFailed(fieldFilter.AttributeName);
            }
        }
        else
        {
            throw OperationFailedException.AttributeDoesNotExist(fieldFilter.AttributeName, GetEntityName());
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
                return Builders<TEntity>.Filter.In(attributeName, value != null ? (IEnumerable<object>)value : Array.Empty<object>());
            case FieldFilterOperator.NotIn:
                return Builders<TEntity>.Filter.Nin(attributeName, value != null ? (IEnumerable<object>)value : Array.Empty<object>());
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
            case FieldFilterOperator.Match:
                var filterMatch = (FilterDefinition<RtRecord>?)value;
                return Builders<TEntity>.Filter.ElemMatch(attributeName, filterMatch);
            default:
                throw new NotSupportedException("Value is not implemented.");
        }
    }
    
    private static string? GetRegex(string? value)
    {
        return value?.Replace("*", "/");
    }
}