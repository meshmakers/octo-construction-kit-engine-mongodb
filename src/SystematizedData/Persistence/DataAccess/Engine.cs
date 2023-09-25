using System;
using System.Collections.Generic;
using System.Linq;
using Meshmakers.Octo.SystematizedData.Persistence.Formulas;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class Engine<TEntity> where TEntity : class, new()
{
    private readonly BsonClassMap<TEntity> _bsonClassMap;
    private readonly List<FilterDefinition<TEntity>> _fieldFilters;
    private readonly List<FilterDefinition<TEntity>> _idFilters;

    protected Engine()
    {
        _idFilters = new List<FilterDefinition<TEntity>>();
        _fieldFilters = new List<FilterDefinition<TEntity>>();

        _bsonClassMap = new BsonClassMap<TEntity>();
        _bsonClassMap.AutoMap();
    }

    protected virtual string GetEntityName()
    {
        return typeof(TEntity).Name;
    }

    protected virtual bool IsAttributeNameValid(string attributeName)
    {
        var memberMap = _bsonClassMap.GetMemberMap(attributeName);
        return memberMap != null;
    }

    protected virtual string? ResolveAttributeName(string attributeName)
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

    protected virtual object? ResolveSearchAttributeValue(string attributeName, object? searchTerm, out bool isEnum)
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
            if (string.IsNullOrWhiteSpace(expressionString) && expressionString != null)
            {
                var expression = new OctoExpression(expressionString);
                var result = expression.calculate();

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

    protected virtual void AddPreFieldFilters(List<FilterDefinition<TEntity>> filters)
    {
        // Indented to left blank to get overridden
    }
    
    protected virtual void AddPostFieldFilters(List<FilterDefinition<TEntity>> filters)
    {
        // Indented to left blank to get overridden
    }

    protected FilterDefinition<TEntity>? CreateFilterDefinitions()
    {
        var filters = new List<FilterDefinition<TEntity>>();

        // Allow to add filter definitions before field filters are applied
        AddPreFieldFilters(filters);
        
        // Add filter for id and fields here
        filters.AddRange(_idFilters.Concat(_fieldFilters));

        // Allow to add filter definitions after field filters are applied
        AddPostFieldFilters(filters);
        
        // if filter constraints exist add them to the pipeline.
        if (filters.Any())
        {
            var filterDefinition = Builders<TEntity>.Filter.Empty;
            if (filters.Any())
            {
                if (filters.Count == 1)
                {
                    filterDefinition = filters.First();
                }
                else
                {
                    filterDefinition = Builders<TEntity>.Filter.And(filters);
                }
            }

            return filterDefinition;
        }

        return null;
    }

    internal void AddIdFilter<TField>(IReadOnlyList<TField>? ids)
    {
        if (ids == null || !ids.Any())
        {
            return;
        }

        _idFilters.Add(Builders<TEntity>.Filter.In(Constants.IdField, ids));
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
                var filter = CreateFilter(resolvedAttributeName, fieldFilter.Operator, resolvedValue);
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

    protected FilterDefinition<TEntity> CreateFilter(string attributeName, FieldFilterOperator comparisonOperator,
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
            default:
                throw new NotImplementedException("Value is not implemented.");
        }
    }

    private static string? GetRegex(string? value)
    {
        return value?.Replace("*", "/");
    }
}