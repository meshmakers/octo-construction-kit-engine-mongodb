using System;
using System.Collections.Generic;
using System.Linq;
using Meshmakers.Octo.SystematizedData.Persistence.Formulas;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public abstract class Query<TEntity> where TEntity : class, new()
{
    private readonly List<FilterDefinition<TEntity>> _attributeSearchFilter;
    private readonly BsonClassMap<TEntity> _bsonClassMap;
    private readonly List<FilterDefinition<TEntity>> _fieldFilters;
    private readonly List<FilterDefinition<TEntity>> _idFilters;

    private readonly List<SortDefinition<TEntity>> _sortDefinitions;

    private FilterDefinition<TEntity> _textFilter;

    protected internal Query(string language = "en")
    {
        Language = language;

        _idFilters = new List<FilterDefinition<TEntity>>();
        _fieldFilters = new List<FilterDefinition<TEntity>>();
        _attributeSearchFilter = new List<FilterDefinition<TEntity>>();
        _sortDefinitions = new List<SortDefinition<TEntity>>();

        _bsonClassMap = new BsonClassMap<TEntity>();
        _bsonClassMap.AutoMap();
    }

    public string Language { get; }

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
        return memberMap?.ElementName;
    }

    protected virtual string GetEntityName()
    {
        return typeof(TEntity).Name;
    }


    protected void AddFilterConstraintsToPipeline(IList<IPipelineStageDefinition> pipelineStageDefinitions)
    {
        var filters = new List<FilterDefinition<TEntity>>();
        if (_attributeSearchFilter.Any())
        {
            if (_attributeSearchFilter.Count > 1)
            {
                filters.Add(Builders<TEntity>.Filter.Or(_attributeSearchFilter));
            }
            else
            {
                filters.Add(_attributeSearchFilter.First());
            }
        }

        // Add filter for id and fields here
        filters.AddRange(_idFilters.Concat(_fieldFilters));

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

            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(filterDefinition));
        }
    }

    protected void AddTextFilterConstraintsToPipeline(IList<IPipelineStageDefinition> pipelineStageDefinitions)
    {
        if (_textFilter != null)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(_textFilter));
            pipelineStageDefinitions.Add(
                PipelineStageDefinitionBuilder.Sort(Builders<TEntity>.Sort.MetaTextScore("score")));
        }
    }

    protected void AddSortConstraintsToPipeline(IList<IPipelineStageDefinition> pipelineStageDefinitions)
    {
        if (_sortDefinitions.Any())
        {
            var sortDefinition = Builders<TEntity>.Sort.Combine(_sortDefinitions);
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Sort(sortDefinition));
        }
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
                    (IEnumerable<object>)resolvedValue));
            }
            else
            {
                var filter = CreateFilter(resolvedAttributeName, fieldFilter.Operator, resolvedValue);
                _fieldFilters.Add(filter);
            }
        }
        else
        {
            throw new OperationFailedException(
                $"Attribute '{fieldFilter.AttributeName}' does not exist on type '{GetEntityName()}'");
        }
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
                .Where(x => x.ToLower().Contains(searchTerm.ToString()?.ToLower()));

            var values = new List<object>();
            foreach (var nameCandidate in nameCandidates)
            {
                values.Add(Enum.Parse(propertyType, nameCandidate));
            }

            isEnum = true;
            return values.ToArray();
        }

        if (searchTerm.ToString().StartsWith("@"))
        {
            var expression = new OctoExpression(searchTerm.ToString().Substring(1));
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
                throw new OperationFailedException($"Term '{searchTerm}' cannot be evaluated by formula.");
            }
        }

        if (propertyType != null && searchTerm is string)
        {
            isEnum = false;

            if (propertyType == typeof(ObjectId))
            {
                return ObjectId.Parse((string)searchTerm);
            }

            if (propertyType == typeof(DateTime) || propertyType == typeof(DateTime?))
            {
                return DateTime.Parse((string)searchTerm);
            }

            try
            {
                return Convert.ChangeType(searchTerm, propertyType);
            }
            catch (Exception)
            {
                // Indented to not handle exception
            }
        }

        isEnum = false;
        return searchTerm;
    }

    internal void AddTextSearchFilter(TextSearchFilter textSearchFilter)
    {
        if (textSearchFilter?.SearchTerm == null)
        {
            return;
        }

        _textFilter = Builders<TEntity>.Filter.Text(textSearchFilter.SearchTerm.ToString(), new TextSearchOptions
        {
            CaseSensitive = false,
            Language = Language,
            DiacriticSensitive = true
        });
    }

    internal void AddAttributeSearchFilter(AttributeSearchFilter attributeSearchFilter)
    {
        if (attributeSearchFilter?.SearchTerm == null || attributeSearchFilter.AttributeNames == null ||
            !attributeSearchFilter.AttributeNames.Any())
        {
            return;
        }


        // ReSharper disable once PossibleMultipleEnumeration
        var attributeNameList = attributeSearchFilter.AttributeNames.ToList();

        foreach (var attributeName in attributeNameList)
        {
            if (IsAttributeNameValid(attributeName))
            {
                var resolvedAttributeName = ResolveAttributeName(attributeName);
                var resolvedValue = ResolveSearchAttributeValue(attributeName,
                    attributeSearchFilter.SearchTerm, out var isEnum);

                if (isEnum)
                {
                    _attributeSearchFilter.Add(Builders<TEntity>.Filter.AnyIn(resolvedAttributeName,
                        (IEnumerable<object>)resolvedValue));
                }
                else
                {
                    _attributeSearchFilter.Add(CreateFilter(resolvedAttributeName, FieldFilterOperator.Like,
                        resolvedValue));
                }
            }
            else
            {
                throw new OperationFailedException(
                    $"Attribute '{attributeName}' does not exist on type '{GetEntityName()}'");
            }
        }
    }

    internal void AddSortConstraintsToPipeline(IEnumerable<SortOrderItem> sortOrders)
    {
        if (sortOrders == null)
        {
            return;
        }

        var sortOrderList = sortOrders.ToList();
        if (!sortOrderList.Any())
        {
            return;
        }

        foreach (var item in sortOrderList)
        {
            if (!IsAttributeNameValid(item.AttributeName) && item.AttributeName != Constants.IdField)
            {
                throw new OperationFailedException(
                    $"Sort definition contains attribute '{item.AttributeName}', but attribute does not exist on type '{GetEntityName()}'");
            }

            var resolvedAttributeName = ResolveAttributeName(item.AttributeName);

            switch (item.SortOrder)
            {
                case SortOrders.Ascending:
                    _sortDefinitions.Add(Builders<TEntity>.Sort.Ascending(resolvedAttributeName));
                    break;
                case SortOrders.Descending:
                    _sortDefinitions.Add(Builders<TEntity>.Sort.Descending(resolvedAttributeName));
                    break;
                default:
                    continue;
            }
        }
    }

    private FilterDefinition<TEntity> CreateFilter(string attributeName, FieldFilterOperator comparisonOperator,
        object value)
    {
        switch (comparisonOperator)
        {
            case FieldFilterOperator.Equals:
                return Builders<TEntity>.Filter.Eq(attributeName, value);
            case FieldFilterOperator.NotEquals:
                return Builders<TEntity>.Filter.Ne(attributeName, value);
            case FieldFilterOperator.In:
                return Builders<TEntity>.Filter.In(attributeName, (IEnumerable<object>)value);
            case FieldFilterOperator.NotIn:
                return Builders<TEntity>.Filter.Nin(attributeName, (IEnumerable<object>)value);
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
            default:
                throw new NotImplementedException("Value is not implemented.");
        }
    }

    private static string GetRegex(string value)
    {
        return value?.Replace("*", "/");
    }
}
