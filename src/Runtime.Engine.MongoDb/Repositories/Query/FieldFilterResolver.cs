using System.Text.RegularExpressions;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
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
        var memberMap = _bsonClassMap.GetMemberMap(attributePath.ToPascalCase());
        return memberMap != null;
    }

    internal virtual string? ResolveAttributePath(string attributePath)
    {
        if (_bsonClassMap.IdMemberMap?.MemberName == attributePath)
        {
            return Constants.IdField;
        }

        var memberMap = _bsonClassMap.GetMemberMap(attributePath.ToPascalCase());
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

        var propertyType = typeof(TEntity).GetProperty(attributePath.ToPascalCase())?.PropertyType;
        if (propertyType is { IsEnum: true })
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

    internal void AddFieldFilterCriteria(FieldFilterCriteria? fieldFilterCriteria)
    {
        if (fieldFilterCriteria == null)
        {
            return;
        }

        AddFieldFilterCriteria(_fieldFilters, fieldFilterCriteria);
    }

    private void AddFieldFilterCriteria(List<FilterDefinition<TEntity>> fieldFilters, FieldFilterCriteria fieldFilterCriteria)
    {

        List<FilterDefinition<TEntity>> innerFieldFilters = new();

        if (fieldFilterCriteria.FieldFilters != null)
        {
            foreach (var fieldFilter in fieldFilterCriteria.FieldFilters)
            {
                AddFieldFilter(innerFieldFilters, fieldFilter);
            }
        }
        else if (fieldFilterCriteria.NestedFilters != null)
        {
            foreach (var filterCriteria in fieldFilterCriteria.NestedFilters)
            {
                AddFieldFilterCriteria(innerFieldFilters, filterCriteria);
            }
        }

        if (innerFieldFilters.Any())
        {
            switch (fieldFilterCriteria.Operator)
            {
                case LogicalOperator.And:
                    fieldFilters.Add(Builders<TEntity>.Filter.And(innerFieldFilters));
                    break;
                case LogicalOperator.Or:
                    fieldFilters.Add(Builders<TEntity>.Filter.Or(innerFieldFilters));
                    break;
            }
        }
    }

    private void AddFieldFilter(List<FilterDefinition<TEntity>> fieldFilters, FieldFilter fieldFilter)
    {
        if (string.IsNullOrWhiteSpace(fieldFilter.AttributePath))
        {
            return;
        }

        if (IsAttributePathValid(fieldFilter.AttributePath))
        {
            var resolvedAttributePath = ResolveAttributePath(fieldFilter.AttributePath);
            var resolvedValue = ResolveSearchAttributeValue(fieldFilter.AttributePath, fieldFilter.ComparisonValue,
                out var isEnum);

            // Resolve the secondary value if present
            object? resolvedSecondaryValue = null;
            if (fieldFilter.SecondaryValue != null)
            {
                resolvedSecondaryValue = ResolveSearchAttributeValue(fieldFilter.AttributePath, fieldFilter.SecondaryValue,
                    out _);
            }

            if (isEnum)
            {
                fieldFilters.Add(Builders<TEntity>.Filter.AnyIn(resolvedAttributePath,
                    resolvedValue != null ? (IEnumerable<object>)resolvedValue : []));
            }
            else if (!string.IsNullOrWhiteSpace(resolvedAttributePath))
            {
                var filter = CreateScalarFilter(resolvedAttributePath, fieldFilter.Operator, resolvedValue, resolvedSecondaryValue);
                fieldFilters.Add(filter);
            }
            else
            {
                throw OperationFailedException.AttributePathResolutionFailed(fieldFilter.AttributePath);
            }
        }
        else
        {
            throw OperationFailedException.AttributePathDoesNotExist(fieldFilter.AttributePath, GetEntityName());
        }
    }
    
    internal static FilterDefinition<TEntity> CreateScalarFilter(string attributePath, FieldFilterOperator comparisonOperator,
        object? value, object? secondaryValue = null)
    {
        switch (comparisonOperator)
        {
            case FieldFilterOperator.Equals:
                return Builders<TEntity>.Filter.Eq(attributePath, value);
            case FieldFilterOperator.NotEquals:
                return Builders<TEntity>.Filter.Ne(attributePath, value);
            case FieldFilterOperator.In:
                object[] array = ComparisonValueToArray(value);
                return Builders<TEntity>.Filter.In(attributePath, array);
            case FieldFilterOperator.NotIn:
                array = ComparisonValueToArray(value);
                return Builders<TEntity>.Filter.Nin(attributePath, array);
            case FieldFilterOperator.LessThan:
                return Builders<TEntity>.Filter.Lt(attributePath, value);
            case FieldFilterOperator.LessEqualThan:
                return Builders<TEntity>.Filter.Lte(attributePath, value);
            case FieldFilterOperator.GreaterThan:
                return Builders<TEntity>.Filter.Gt(attributePath, value);
            case FieldFilterOperator.GreaterEqualThan:
                return Builders<TEntity>.Filter.Gte(attributePath, value);
            case FieldFilterOperator.Like:
                return Builders<TEntity>.Filter.Regex(attributePath,
                    new BsonRegularExpression(GetRegex(value?.ToString()), "i"));
            case FieldFilterOperator.MatchRegEx:
                return Builders<TEntity>.Filter.Regex(attributePath,
                    new BsonRegularExpression(value?.ToString()));
            case FieldFilterOperator.AnyEq:
                return Builders<TEntity>.Filter.AnyEq(attributePath, value);
            case FieldFilterOperator.AnyLike:
                return Builders<TEntity>.Filter.AnyEq(attributePath, new BsonRegularExpression(GetRegex(value?.ToString()), "i"));
            case FieldFilterOperator.Match:
                if (value is FilterDefinition<RtRecord> filterMatch)
                {
                    return Builders<TEntity>.Filter.ElemMatch(attributePath, filterMatch);
                }
                throw OperationFailedException.MatchFilterValueNotSupported(value);
            case FieldFilterOperator.Contains:
                return Builders<TEntity>.Filter.Regex(attributePath,
                    new BsonRegularExpression($".*{Regex.Escape(value?.ToString() ?? string.Empty)}.*", "i"));
            case FieldFilterOperator.StartsWith:
                return Builders<TEntity>.Filter.Regex(attributePath,
                    new BsonRegularExpression($"^{Regex.Escape(value?.ToString() ?? string.Empty)}", "i"));
            case FieldFilterOperator.EndsWith:
                return Builders<TEntity>.Filter.Regex(attributePath,
                    new BsonRegularExpression($"{Regex.Escape(value?.ToString() ?? string.Empty)}$", "i"));
            case FieldFilterOperator.Between:
                // If secondaryValue is provided, use it as the upper bound
                if (secondaryValue != null)
                {
                    return Builders<TEntity>.Filter.And(
                        Builders<TEntity>.Filter.Gte(attributePath, value),
                        Builders<TEntity>.Filter.Lte(attributePath, secondaryValue)
                    );
                }
                // Fall back to the old behavior of using an array for backward compatibility
                array = ComparisonValueToArray(value);
                if (array.Length >= 2)
                {
                    return Builders<TEntity>.Filter.And(
                        Builders<TEntity>.Filter.Gte(attributePath, array[0]),
                        Builders<TEntity>.Filter.Lte(attributePath, array[1])
                    );
                }
                throw OperationFailedException.OperatorRequiresSecondaryValue(comparisonOperator);
            case FieldFilterOperator.IsNull:
                return Builders<TEntity>.Filter.Eq<object?>(attributePath, null);
            case FieldFilterOperator.IsNotNull:
                return Builders<TEntity>.Filter.Ne<object?>(attributePath, null);
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
                array = stringValue.Split(',').ToArray<object>();
            }
        }
        else if (value is OctoObjectId octoObjectId)
        {
            array = [octoObjectId];
        }
        else if (value is DateTime dateTime)
        {
            array = [dateTime];
        }
        else if (value is DateTimeOffset dateTimeOffset)
        {
            array = [dateTimeOffset.DateTime];
        }
        else if (value is IEnumerable<string> stringEnumerable)
        {
            array = stringEnumerable.ToArray<object>();
        }
        else if (value is IEnumerable<object> enumerable)
        {
            array = enumerable.ToArray();
        }
        else if (value is IEnumerable<int> intEnumerable)
        {
            array = intEnumerable.Cast<object>().ToArray();
        }
        else if (value is IEnumerable<long> longEnumerable)
        {
            array = longEnumerable.Cast<object>().ToArray();
        }
        else if (value is IEnumerable<double> doubleEnumerable)
        {
            array = doubleEnumerable.Cast<object>().ToArray();
        }
        else if (value is IEnumerable<bool> boolEnumerable)
        {
            array = boolEnumerable.Cast<object>().ToArray();
        }
        else if (value is IEnumerable<DateTime> dateTimeEnumerable)
        {
            array = dateTimeEnumerable.Cast<object>().ToArray();
        }

        return array;
    }

    private static string? GetRegex(string? value)
    {
        return value?.Replace("*", ".*");
    }
}
