using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class Engine<TEntity> where TEntity : class, new()
{
    private readonly List<FilterDefinition<TEntity>> _idFilters;
    protected readonly FieldFilterResolver<TEntity> _fieldFilterResolver;

    protected Engine(FieldFilterResolver<TEntity> fieldFilterResolver)
    {
        _idFilters = new List<FilterDefinition<TEntity>>();
        _fieldFilterResolver = fieldFilterResolver;
    }

    internal void AddFieldFilters(ICollection<FieldFilter>? fieldFilters)
    {
        _fieldFilterResolver.AddFieldFilters(fieldFilters);
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
        filters.AddRange(_idFilters.Concat(_fieldFilterResolver.FilterDefinitions));

        // Allow to add filter definitions after field filters are applied
        AddPostFieldFilters(filters);

        // if filter constraints exist add them to the pipeline.
        if (filters.Any())
        {
            if (filters.Count == 1)
            {
                return filters.First();
            }

            return Builders<TEntity>.Filter.And(filters);
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
}