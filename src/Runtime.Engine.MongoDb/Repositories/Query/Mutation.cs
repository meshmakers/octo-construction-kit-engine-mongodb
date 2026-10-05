using System.Collections.ObjectModel;

using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.Repositories;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class Mutation<TEntity> : Engine<TEntity> where TEntity : RtEntity, new()
{
    private readonly ICkCacheService _ckCacheService;
    private readonly CkTypeGraph _ckTypeGraph;
    private readonly IBulkRtMutation _bulkRtMutation;
    private readonly IMongoDbRepositoryDataSource _mongoDbRepositoryDataSource;
    private readonly DeleteOptions _deleteOptions;

    public Mutation(ICkCacheService ckCacheService, string tenantId, CkTypeGraph ckTypeGraph, IBulkRtMutation bulkRtMutation,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource, DeleteOptions deleteOptions)
        : base(new RtEntityFieldFilterResolver<TEntity>(ckCacheService, tenantId, ckTypeGraph))
    {
        _ckCacheService = ckCacheService;
        _ckTypeGraph = ckTypeGraph;
        _bulkRtMutation = bulkRtMutation;
        _mongoDbRepositoryDataSource = mongoDbRepositoryDataSource;
        _deleteOptions = deleteOptions;
    }

    public async Task ReplaceOneAsync(IOctoSession session, TEntity rtEntity)
    {
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions == null)
        {
            throw TenantRepositoryException.NoFilterDefinitions();
        }

        var rtCollection = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(_ckTypeGraph);
        var entities = await rtCollection.FindManyAsync(session, filterDefinitions);
        if (entities.Count != 1)
        {
            throw TenantRepositoryException.EntityFilterReturnNotExactlyOne();
        }

        var entityUpdateInfoList = entities.Select(entityToUpdate =>
            EntityUpdateInfo<TEntity>.CreateReplace(entityToUpdate.ToRtEntityId(), rtEntity)).ToList();

        await _bulkRtMutation.ApplyChangesAsync(session, _mongoDbRepositoryDataSource, _ckCacheService, entityUpdateInfoList,
            new Collection<AssociationUpdateInfo>(), BulkRtMutationOptions.Default);
    }

    public async Task UpdateOneAsync(IOctoSession session, TEntity rtEntity)
    {
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions == null)
        {
            throw TenantRepositoryException.NoFilterDefinitions();
        }

        var rtCollection = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(_ckTypeGraph);
        var entities = await rtCollection.FindManyAsync(session, filterDefinitions);
        if (entities.Count != 1)
        {
            throw TenantRepositoryException.EntityFilterReturnNotExactlyOne();
        }

        var entityUpdateInfoList = entities.Select(entityToUpdate =>
            EntityUpdateInfo<TEntity>.CreateUpdate(entityToUpdate.ToRtEntityId(), rtEntity)).ToList();

        await _bulkRtMutation.ApplyChangesAsync(session, _mongoDbRepositoryDataSource, _ckCacheService, entityUpdateInfoList,
            new Collection<AssociationUpdateInfo>(), BulkRtMutationOptions.Default);
    }

    public async Task DeleteOneAsync(IOctoSession session)
    {
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions == null)
        {
            throw TenantRepositoryException.NoFilterDefinitions();
        }

        var rtCollection = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(_ckTypeGraph);
        var entities = await rtCollection.FindManyAsync(session, filterDefinitions);
        if (entities.Count != 1)
        {
            throw TenantRepositoryException.EntityFilterReturnNotExactlyOne();
        }

        var entityUpdateInfoList = entities.Select(entityToUpdate =>
            EntityUpdateInfo<TEntity>.CreateDelete(entityToUpdate.ToRtEntityId())).ToList();

        await _bulkRtMutation.ApplyChangesAsync(session, _mongoDbRepositoryDataSource, _ckCacheService, entityUpdateInfoList,
            new Collection<AssociationUpdateInfo>(), BulkRtMutationOptions.FromDeleteOptions(_deleteOptions));
    }

    public async Task DeleteManyAsync(IOctoSession session)
    {
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions == null)
        {
            throw TenantRepositoryException.NoFilterDefinitions();
        }

        var rtCollection = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(_ckTypeGraph);
        var entities = await rtCollection.FindManyAsync(session, filterDefinitions);

        var entityUpdateInfoList = entities.Select(entityToUpdate =>
            EntityUpdateInfo<TEntity>.CreateDelete(entityToUpdate.ToRtEntityId())).ToList();

        await _bulkRtMutation.ApplyChangesAsync(session, _mongoDbRepositoryDataSource, _ckCacheService, entityUpdateInfoList,
            new Collection<AssociationUpdateInfo>(), BulkRtMutationOptions.FromDeleteOptions(_deleteOptions));
    }

    public async Task UpdateManyAsync(IOctoSession session, TEntity rtEntity)
    {
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions == null)
        {
            throw TenantRepositoryException.NoFilterDefinitions();
        }

        var rtCollection = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(_ckTypeGraph);
        var entities = await rtCollection.FindManyAsync(session, filterDefinitions);

        // One copy of the update per target: BulkRtMutation sets the target's rtId on the entity and the
        // secret write step carries stored secrets over into it (AB#5533). With one shared object every
        // update request ended up addressing the last target, and a secret carried over from entity A
        // could be written to entity B.
        var entityUpdateInfoList = entities.Select(entityToUpdate =>
            EntityUpdateInfo<TEntity>.CreateUpdate(entityToUpdate.ToRtEntityId(), CopyEntity(rtEntity))).ToList();

        await _bulkRtMutation.ApplyChangesAsync(session, _mongoDbRepositoryDataSource, _ckCacheService, entityUpdateInfoList,
            new Collection<AssociationUpdateInfo>(), BulkRtMutationOptions.Default);
    }

    /// <summary>
    ///     Copies an entity for one update target: the system fields and the attribute values, with records
    ///     and arrays copied deeply (the write steps change them in place). Scalars and
    ///     <see cref="RtSecretValue" />s are immutable and shared.
    /// </summary>
    internal static TEntity CopyEntity(TEntity source)
    {
        var copy = new TEntity
        {
            RtId = source.RtId,
            CkTypeId = source.CkTypeId,
            RtCreationDateTime = source.RtCreationDateTime,
            RtChangedDateTime = source.RtChangedDateTime,
            RtArchivedDateTime = source.RtArchivedDateTime,
            RtWellKnownName = source.RtWellKnownName,
            RtCreatedBy = source.RtCreatedBy,
            RtDisplayName = source.RtDisplayName,
            RtDisplayDescription = source.RtDisplayDescription,
            RtVersion = source.RtVersion,
            RtState = source.RtState
        };
        foreach (var (name, value) in source.Attributes)
        {
            copy.SetAttributeRawValue(name, CopyValue(value));
        }

        return copy;
    }

    private static object? CopyValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case RtRecord record:
                return new RtRecord(record.CkRecordId,
                    record.Attributes.ToDictionary(p => p.Key, p => CopyValue(p.Value), StringComparer.Ordinal));
            case string or byte[] or System.Collections.IDictionary:
                return value;
            case IEnumerable<RtRecord> records:
                return records.Select(r => (RtRecord)CopyValue(r)!).ToList();
            case IEnumerable<string> strings:
                return strings.ToList();
            case System.Collections.IEnumerable elements:
                return elements.Cast<object?>().Select(CopyValue).ToList();
            default:
                return value;
        }
    }
}
