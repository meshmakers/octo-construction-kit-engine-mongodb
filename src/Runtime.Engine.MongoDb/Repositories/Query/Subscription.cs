using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.ConstructionKit.Contracts;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

/// <summary>How a subscription that opted into delete events matches them (a delete event has no full document).</summary>
internal enum DeleteEventMatch
{
    /// <summary>No delete events (the default, and the behaviour before the opt-in existed).</summary>
    None,

    /// <summary>On the pre-image of the deleted document: exact for every type and filter.</summary>
    PreImage,

    /// <summary>On the document key only: for a subscription on the whole collection without field filters.</summary>
    DocumentKey
}

internal class Subscription<TEntity>(
    ICkCacheService ckCacheService,
    string tenantId,
    CkTypeGraph ckTypeGraph,
    IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    : Engine<TEntity>(new RtEntityFieldFilterResolver<TEntity>(ckCacheService, tenantId, ckTypeGraph))
    where TEntity : RtEntity, new()
{
    private readonly FieldFilterResolver<TEntity> _beforeFieldFilterResolver =
        new RtEntityFieldFilterResolver<TEntity>(ckCacheService, tenantId, ckTypeGraph);

    internal void AddBeforeFieldFilterCriteria(FieldFilterCriteria? fieldFilterCriteria)
    {
        _beforeFieldFilterResolver.AddFieldFilterCriteria(fieldFilterCriteria);
    }
    
    public IUpdateStream<TEntity> WatchRtEntitiesAsync(UpdateTypes updateTypes, CancellationToken cancellationToken)
    {
        var beforeFilterDefinitions = CreateBeforeFilterDefinitions();
        var filterDefinitions = CreateFilterDefinitions();

        var rtCollection = mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(ckTypeGraph);

        return rtCollection.WatchAsync(updateTypes,
            filterDefinitions == null
                ? null
                : () => Builders<ChangeStreamDocument<TEntity>>.Filter.Inject(Constants.ChangeStreamFullDocument, filterDefinitions),
            beforeFilterDefinitions == null
                ? null
                : () => Builders<ChangeStreamDocument<TEntity>>.Filter.Inject(Constants.ChangeStreamFullDocumentBeforeChange, beforeFilterDefinitions),
            cancellationToken);
    }

    /// <summary>
    ///     Like <see cref="WatchRtEntitiesAsync(UpdateTypes, CancellationToken)" />, plus the delete events the subscriber
    ///     opted into (<see cref="WatchStreamFilter.IncludeDeleteEvents" />). The events of the other operation types are
    ///     matched exactly as without the opt-in; a delete event has no full document, so it is matched by a branch of
    ///     its own, joined with <c>$or</c>:
    ///     <list type="bullet">
    ///         <item><see cref="DeleteEventMatch.PreImage" />: the pre-image (<c>fullDocumentBeforeChange</c>) must
    ///         match the type set, the RtId and the field filters, and the before-field filters - exact for every type
    ///         of a shared collection;</item>
    ///         <item><see cref="DeleteEventMatch.DocumentKey" />: only for a subscription on the collection root without
    ///         field filters, where every document of the collection belongs to the watched type set; matched by
    ///         <c>documentKey._id</c> when an RtId is watched.</item>
    ///     </list>
    /// </summary>
    public IUpdateStream<TEntity> WatchRtEntitiesAsync(UpdateTypes updateTypes, DeleteEventMatch deleteEventMatch,
        OctoObjectId? rtId, CancellationToken cancellationToken)
    {
        if (deleteEventMatch == DeleteEventMatch.None ||
            (updateTypes != UpdateTypes.Undefined && !updateTypes.HasFlag(UpdateTypes.Delete)))
        {
            return WatchRtEntitiesAsync(updateTypes, cancellationToken);
        }

        var beforeFilterDefinitions = CreateBeforeFilterDefinitions();
        var filterDefinitions = CreateFilterDefinitions();
        var builder = Builders<ChangeStreamDocument<TEntity>>.Filter;

        // The other operation types: today's filter, unchanged (post-image AND pre-image constraints).
        var changeFilter = BuildExtensions.ComposeWatchFilter(
            filterDefinitions == null ? null : builder.Inject(Constants.ChangeStreamFullDocument, filterDefinitions),
            beforeFilterDefinitions == null
                ? null
                : builder.Inject(Constants.ChangeStreamFullDocumentBeforeChange, beforeFilterDefinitions));

        var deleteFilters = new List<FilterDefinition<ChangeStreamDocument<TEntity>>>
        {
            builder.Eq(x => x.OperationType, ChangeStreamOperationType.Delete)
        };
        if (deleteEventMatch == DeleteEventMatch.PreImage)
        {
            if (filterDefinitions != null)
            {
                deleteFilters.Add(builder.Inject(Constants.ChangeStreamFullDocumentBeforeChange, filterDefinitions));
            }

            if (beforeFilterDefinitions != null)
            {
                deleteFilters.Add(builder.Inject(Constants.ChangeStreamFullDocumentBeforeChange,
                    beforeFilterDefinitions));
            }
        }
        else if (rtId.HasValue)
        {
            deleteFilters.Add(builder.Eq(Constants.ChangeStreamDocumentKey + "." + Constants.IdField,
                (BsonValue)new BsonObjectId(ObjectId.Parse(rtId.Value.ToString()))));
        }

        var deleteFilter = builder.And(deleteFilters);
        FilterDefinition<ChangeStreamDocument<TEntity>> combined = changeFilter == null
            ? deleteFilter
            : builder.Or(changeFilter, deleteFilter);

        var rtCollection = mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(ckTypeGraph);
        return rtCollection.WatchAsync(updateTypes, () => combined, null, cancellationToken);
    }

    protected override void AddPreFieldFilters(List<FilterDefinition<TEntity>> filters)
    {
        base.AddPreFieldFilters(filters);

        // Add filter for ck type and derived ones
        var ckTypeIds = ckTypeGraph.GetAllDerivedTypes(true).Select(t=> t.ToRtCkId());
        filters.Add(Builders<TEntity>.Filter.In(f => f.CkTypeId, ckTypeIds));
    }
    
    private FilterDefinition<TEntity>? CreateBeforeFilterDefinitions()
    {
        var filters = new List<FilterDefinition<TEntity>>();

        filters.AddRange(_beforeFieldFilterResolver.FilterDefinitions);

        // if filter constraints exist, add them to the pipeline.
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
}
