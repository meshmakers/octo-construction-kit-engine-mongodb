using System.Collections.ObjectModel;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
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

    public Mutation(ICkCacheService ckCacheService, string tenantId, CkTypeGraph ckTypeGraph, IBulkRtMutation bulkRtMutation,
        IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
        : base(new RtEntityFieldFilterResolver<TEntity>(ckCacheService, tenantId, ckTypeGraph))
    {
        _ckCacheService = ckCacheService;
        _ckTypeGraph = ckTypeGraph;
        _bulkRtMutation = bulkRtMutation;
        _mongoDbRepositoryDataSource = mongoDbRepositoryDataSource;
    }

    public async Task ReplaceOneAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, TEntity rtEntity)
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
            new Collection<AssociationUpdateInfo>());
    }

    public async Task UpdateOneAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, TEntity rtEntity)
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
            new Collection<AssociationUpdateInfo>());
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
            new Collection<AssociationUpdateInfo>());
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
            new Collection<AssociationUpdateInfo>());
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

        var entityUpdateInfoList = entities.Select(entityToUpdate =>
            EntityUpdateInfo<TEntity>.CreateUpdate(entityToUpdate.ToRtEntityId(), rtEntity)).ToList();

        await _bulkRtMutation.ApplyChangesAsync(session, _mongoDbRepositoryDataSource, _ckCacheService, entityUpdateInfoList,
            new Collection<AssociationUpdateInfo>());
    }
}