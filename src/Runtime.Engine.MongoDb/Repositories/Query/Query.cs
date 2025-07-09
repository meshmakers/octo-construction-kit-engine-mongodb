using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal abstract class Query<TEntity> : Engine<TEntity> where TEntity : class, new()
{
    protected readonly string _tenantId;
    private readonly List<FilterDefinition<TEntity>> _attributeSearchFilter;
    private readonly List<SortDefinition<TEntity>> _sortDefinitions;
    private FilterDefinition<TEntity>? _textFilter;

    protected internal Query(FieldFilterResolver<TEntity> fieldFilterResolver, string tenantId, string language = "en")
        : base(fieldFilterResolver)
    {
        _tenantId = tenantId;
        Language = language;

        _attributeSearchFilter = new List<FilterDefinition<TEntity>>();
        _sortDefinitions = new List<SortDefinition<TEntity>>();
    }

    private string Language { get; }

    protected FieldGroupBy? GroupBy { get; private set; }

    protected override void AddPreFieldFilters(List<FilterDefinition<TEntity>> filters)
    {
        base.AddPreFieldFilters(filters);

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
    }

    protected virtual void AddPreStagesToPipelines(IList<IPipelineStageDefinition> pipelineStageDefinitions)
    {
        if (_textFilter != null)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(_textFilter));
            pipelineStageDefinitions.Add(
                PipelineStageDefinitionBuilder.Sort(Builders<TEntity>.Sort.MetaTextScore("score")));
        }
    }

    protected virtual void AddPostStagesToPipeline(IList<IPipelineStageDefinition> pipelineStageDefinitions)
    {
        if (_sortDefinitions.Any())
        {
            var sortDefinition = Builders<TEntity>.Sort.Combine(_sortDefinitions);
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Sort(sortDefinition));
        }
    }
    
    protected virtual IEnumerable<GroupingResult>? CalculateGrouping(IEnumerable<TEntity> resultList)
    {
        return null;
    }


    internal void AddTextSearchFilter(TextSearchFilter? textSearchFilter)
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

    internal void AddAttributeSearchFilter(AttributeSearchFilter? attributeSearchFilter)
    {
        if (attributeSearchFilter?.SearchTerm == null ||
            !attributeSearchFilter.AttributePaths.Any())
        {
            return;
        }

        // ReSharper disable once PossibleMultipleEnumeration
        var attributePathList = attributeSearchFilter.AttributePaths.ToList();

        foreach (var attributePath in attributePathList)
        {
            if (_fieldFilterResolver.IsAttributePathValid(attributePath))
            {
                var resolveAttributePath = _fieldFilterResolver.ResolveAttributePath(attributePath);
                var resolvedValue = _fieldFilterResolver.ResolveSearchAttributeValue(attributePath,
                    attributeSearchFilter.SearchTerm, out var isEnum);

                if (isEnum)
                {
                    _attributeSearchFilter.Add(Builders<TEntity>.Filter.AnyIn(resolveAttributePath,
                        resolvedValue != null ? (IEnumerable<object>)resolvedValue : []));
                }
                else if (!string.IsNullOrWhiteSpace(resolveAttributePath))
                {
                    if (resolvedValue is not string)
                    {
                        _attributeSearchFilter.Add(_fieldFilterResolver.CreateScalarFilter(resolveAttributePath, FieldFilterOperator.Equals,
                            resolvedValue));
                    }
                    else
                    {
                        _attributeSearchFilter.Add(_fieldFilterResolver.CreateScalarFilter(resolveAttributePath, FieldFilterOperator.Like,
                            resolvedValue));
                    }
                }
                else
                {
                    throw OperationFailedException.AttributeNameResolutionFailed(attributePath);
                }
            }
            else
            {
                throw OperationFailedException.AttributeDoesNotExist(attributePath, _fieldFilterResolver.GetEntityName());
            }
        }
    }

    internal void AddPostStagesToPipeline(IEnumerable<SortOrderItem>? sortOrders)
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
            if (!_fieldFilterResolver.IsAttributePathValid(item.AttributePath) && item.AttributePath != Constants.IdField)
            {
                throw InvalidAttributeException.SortDefinitionContainsInvalidAttribute(item.AttributePath, _fieldFilterResolver.GetEntityName());
            }

            var resolvedAttributeName = _fieldFilterResolver.ResolveAttributePath(item.AttributePath);

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

    internal void AddGrouping(FieldGroupBy? groupByDto)
    {
        if (groupByDto == null)
        {
            return;
        }

        GroupBy = groupByDto;
    }
}
