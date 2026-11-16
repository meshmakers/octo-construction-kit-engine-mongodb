using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal abstract class Query<TEntity> : Engine<TEntity> where TEntity : class, new()
{
    private readonly List<FilterDefinition<TEntity>> _attributeSearchFilter;
    private readonly List<SortDefinition<TEntity>> _sortDefinitions;
    private readonly List<(string Path, bool Descending)> _sortKeys = new();
    private FilterDefinition<TEntity>? _textFilter;

    protected internal Query(FieldFilterResolver<TEntity> fieldFilterResolver, string language = "en")
        : base(fieldFilterResolver)
    {
        Language = language;

        _attributeSearchFilter = new List<FilterDefinition<TEntity>>();
        _sortDefinitions = new List<SortDefinition<TEntity>>();
    }

    private string Language { get; }

    protected FieldAggregationInput? FieldAggregation { get; private set; }
    protected AggregationInput? ResultAggregation { get; private set; }

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

    /// <summary>
    ///     The stages of <see cref="AddPreStagesToPipelines" /> that narrow the result set, without the stages that
    ///     only order it (AB#8895: a count-only query never sorts). Override together with
    ///     <see cref="AddPreStagesToPipelines" />, so that the count always covers exactly the entities the item
    ///     query returns.
    /// </summary>
    protected virtual void AddCountPreStagesToPipeline(IList<IPipelineStageDefinition> pipelineStageDefinitions)
    {
        if (_textFilter != null)
        {
            pipelineStageDefinitions.Add(PipelineStageDefinitionBuilder.Match(_textFilter));
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

    /// <summary>
    /// Gets the sort stage as a BsonDocument for use in manually built pipelines.
    /// Returns null if no sort definitions are configured.
    /// </summary>
    protected BsonDocument? GetSortStageBsonDocument()
    {
        if (!_sortDefinitions.Any())
        {
            return null;
        }

        var sortDefinition = Builders<TEntity>.Sort.Combine(_sortDefinitions);
        var renderArgs = new RenderArgs<TEntity>(
            BsonSerializer.SerializerRegistry.GetSerializer<TEntity>(),
            BsonSerializer.SerializerRegistry);
        var renderedSort = sortDefinition.Render(renderArgs);
        return new BsonDocument("$sort", renderedSort);
    }

    /// <summary>
    /// Returns sort stage definitions for use in sub-pipelines (e.g., $facet page branch).
    /// </summary>
    protected IReadOnlyList<IPipelineStageDefinition> CreateSortStageDefinitions()
    {
        if (!_sortDefinitions.Any())
        {
            return [];
        }

        var sortDefinition = Builders<TEntity>.Sort.Combine(_sortDefinitions);
        return [PipelineStageDefinitionBuilder.Sort(sortDefinition)];
    }

    /// <summary>
    ///     True when the caller's sort orders by <c>_id</c> (rtId) on one of its keys: every key after it can never
    ///     decide, so the order is total and needs no tiebreak.
    /// </summary>
    internal bool IsSortUnique => _sortKeys.Any(k => k.Path == Constants.IdField);

    /// <summary>The path of the caller's first sort key, or null without a caller sort.</summary>
    internal string? FirstSortKeyPath => _sortKeys.Count > 0 ? _sortKeys[0].Path : null;

    /// <summary>
    ///     True when the stages before the page sort already order the result and there is no caller sort: a
    ///     <c>$geoNear</c> (nearest first) or the text-score sort of a text search (best match first). A page then keeps
    ///     that order instead of the <c>_id</c> tiebreak, which would replace it.
    /// </summary>
    protected virtual bool PreStagesDefineOrder => _textFilter != null;

    /// <summary>
    ///     The sort of a paged read (AB#8960): the caller's sort keys, then <c>_id</c> in the direction of the last
    ///     caller key - so that one index <c>{…, attr, _id}</c> serves the page forwards and backwards - unless the sort
    ///     is already unique (<see cref="IsSortUnique" />); without a caller sort <c>_id</c> ascending alone, or null
    ///     (no sort) when <see cref="PreStagesDefineOrder" />. Only a total order makes <c>$skip</c>/<c>$limit</c>
    ///     deterministic: over equal sort keys (or no sort) MongoDB returns the ties in an order that can change from
    ///     one page to the next, so pages would repeat or miss entities. Unpaged reads keep the caller's sort as it is
    ///     (<see cref="AddPostStagesToPipeline(IList{IPipelineStageDefinition})" />), and so do the association
    ///     navigations, whose entity-driven path (AB#4369) depends on an index-backed sort with a <c>$lookup</c>
    ///     between <c>$sort</c> and <c>$limit</c>.
    /// </summary>
    protected SortDefinition<TEntity>? CreatePagingSortDefinition()
    {
        if (_sortKeys.Count == 0)
        {
            return PreStagesDefineOrder ? null : Builders<TEntity>.Sort.Ascending(Constants.IdField);
        }

        var keys = new List<SortDefinition<TEntity>>(_sortDefinitions);
        if (!IsSortUnique)
        {
            keys.Add(_sortKeys[^1].Descending
                ? Builders<TEntity>.Sort.Descending(Constants.IdField)
                : Builders<TEntity>.Sort.Ascending(Constants.IdField));
        }

        return Builders<TEntity>.Sort.Combine(keys);
    }

    /// <summary>
    ///     The sort stages of a page: with <paramref name="idTiebreak" /> the <c>$sort</c> of
    ///     <see cref="CreatePagingSortDefinition" /> (none when it is null), otherwise the caller's sort as it is (none
    ///     without one).
    /// </summary>
    protected IReadOnlyList<IPipelineStageDefinition> CreatePageSortStageDefinitions(bool idTiebreak)
    {
        if (!idTiebreak)
        {
            return CreateSortStageDefinitions();
        }

        var sort = CreatePagingSortDefinition();
        return sort == null ? [] : [PipelineStageDefinitionBuilder.Sort(sort)];
    }

    /// <summary>
    /// The configured sort keys as resolved MongoDB field paths with their direction, in sort order
    /// (AB#7019: the application-side merge of an interface query compares rows on exactly these keys).
    /// </summary>
    internal IReadOnlyList<(string Path, bool Descending)> SortKeys => _sortKeys;

    /// <summary>
    /// The configured sort definitions combined, or null when no sort order is configured.
    /// </summary>
    internal SortDefinition<TEntity>? CombinedSortDefinition =>
        _sortDefinitions.Any() ? Builders<TEntity>.Sort.Combine(_sortDefinitions) : null;

    /// <summary>
    /// Indicates whether sort definitions have been configured.
    /// </summary>
    protected bool HasSortDefinitions => _sortDefinitions.Any();

    /// <summary>
    ///     Hook for every materialised result page before aggregations are computed (AB#5533: the
    ///     RT queries normalise legacy strings in Secret slots to <c>RtSecretValue.LegacyPlaintext</c>).
    /// </summary>
    protected virtual void OnResultsMaterialized(IEnumerable<TEntity> resultList)
    {
    }

    /// <summary>
    ///     Runs <see cref="OnResultsMaterialized" /> and then <see cref="CalculateAggregations" /> - the
    ///     single funnel every query result passes before it is returned.
    /// </summary>
    protected (AggregationResult?, IEnumerable<FieldAggregationResult>?) FinalizeResults(
        IEnumerable<TEntity> resultList)
    {
        OnResultsMaterialized(resultList);
        return CalculateAggregations(resultList);
    }

    protected virtual (AggregationResult?, IEnumerable<FieldAggregationResult>?) CalculateAggregations(
        IEnumerable<TEntity> resultList)
    {
        return (null, null);
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
                // AB#5533: never LIKE-match a Secret attribute (ciphertext or legacy plaintext).
                _fieldFilterResolver.EnsureNotSecretAttribute(attributePath, "attribute search");

                var resolveAttributePath = _fieldFilterResolver.ResolveAttributePath(attributePath);
                // For attribute search, the operator is determined after resolving the value:
                // - String values use LIKE, non-string values use EQUALS
                // We pass LIKE here as a hint for string-based fields like RtId
                var resolvedValue = _fieldFilterResolver.ResolveSearchAttributeValue(attributePath,
                    attributeSearchFilter.SearchTerm, FieldFilterOperator.Like, out var isEnum);

                if (isEnum)
                {
                    _attributeSearchFilter.Add(Builders<TEntity>.Filter.AnyIn(resolveAttributePath,
                        resolvedValue != null ? (IEnumerable<object>)resolvedValue : []));
                }
                else if (!string.IsNullOrWhiteSpace(resolveAttributePath))
                {
                    if (resolvedValue is not string)
                    {
                        _attributeSearchFilter.Add(FieldFilterResolver<TEntity>.CreateScalarFilter(resolveAttributePath, FieldFilterOperator.Equals,
                            resolvedValue));
                    }
                    else
                    {
                        _attributeSearchFilter.Add(FieldFilterResolver<TEntity>.CreateScalarFilter(resolveAttributePath, FieldFilterOperator.Like,
                            resolvedValue));
                    }
                }
                else
                {
                    throw OperationFailedException.AttributePathResolutionFailed(attributePath);
                }
            }
            else
            {
                throw OperationFailedException.AttributePathDoesNotExist(attributePath, _fieldFilterResolver.GetEntityName());
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

            // AB#5533: sorting by a Secret attribute would order by ciphertext / leak plaintext order.
            _fieldFilterResolver.EnsureNotSecretAttribute(item.AttributePath, "sort");

            var resolvedAttributeName = _fieldFilterResolver.ResolveAttributePath(item.AttributePath);

            switch (item.SortOrder)
            {
                case SortOrders.Ascending:
                    _sortDefinitions.Add(Builders<TEntity>.Sort.Ascending(resolvedAttributeName));
                    _sortKeys.Add((resolvedAttributeName!, false));
                    break;
                case SortOrders.Descending:
                    _sortDefinitions.Add(Builders<TEntity>.Sort.Descending(resolvedAttributeName));
                    _sortKeys.Add((resolvedAttributeName!, true));
                    break;
                default:
                    continue;
            }
        }
    }

    internal void AddFieldAggregation(FieldAggregationInput? fieldAggregationInput)
    {
        if (fieldAggregationInput == null)
        {
            return;
        }

        // AB#5533: no group-by or aggregate over a Secret attribute.
        foreach (var path in fieldAggregationInput.GroupByAttributePathList)
        {
            _fieldFilterResolver.EnsureNotSecretAttribute(path, "group-by");
        }

        EnsureNoSecretAggregationPath(fieldAggregationInput);
        FieldAggregation = fieldAggregationInput;
    }

    internal void AddResultAggregation(AggregationInput? aggregationInput)
    {
        if (aggregationInput == null)
        {
            return;
        }

        EnsureNoSecretAggregationPath(aggregationInput);
        ResultAggregation = aggregationInput;
    }

    private void EnsureNoSecretAggregationPath(AggregationInput aggregationInput)
    {
        foreach (var path in aggregationInput.CountAttributePathList
                     .Concat(aggregationInput.MaxValueAttributePathList)
                     .Concat(aggregationInput.MinValueAttributePathList)
                     .Concat(aggregationInput.AvgAttributePathList)
                     .Concat(aggregationInput.SumAttributePathList))
        {
            _fieldFilterResolver.EnsureNotSecretAttribute(path, "aggregation");
        }
    }
}
