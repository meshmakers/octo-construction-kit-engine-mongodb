using System.Collections.ObjectModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.Repositories;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

public class Mutation<TEntity> : Engine<TEntity> where TEntity : RtEntity, new()
{
    private readonly IBulkRtMutation _bulkRtMutation;
    private readonly IMongoDbRepositoryDataSource _mongoDbRepositoryDataSource;

    public Mutation(IBulkRtMutation bulkRtMutation, IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    {
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

        var rtCollection = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(ckTypeId);
        var entities = await rtCollection.FindManyAsync(session, filterDefinitions);
        if (entities.Count != 1)
        {
            throw TenantRepositoryException.EntityFilterReturnNotExactlyOne();
        }

        var entityUpdateInfoList = entities.Select(entityToUpdate =>
            EntityUpdateInfo<TEntity>.CreateReplace(entityToUpdate.ToRtEntityId(), rtEntity)).ToList();

        await _bulkRtMutation.ApplyChangesAsync(session, _mongoDbRepositoryDataSource, entityUpdateInfoList,
            new Collection<AssociationUpdateInfo>());
    }

    public async Task UpdateOneAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, TEntity rtEntity)
    {
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions == null)
        {
            throw TenantRepositoryException.NoFilterDefinitions();
        }

        var rtCollection = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(ckTypeId);
        var entities = await rtCollection.FindManyAsync(session, filterDefinitions);
        if (entities.Count != 1)
        {
            throw TenantRepositoryException.EntityFilterReturnNotExactlyOne();
        }

        var entityUpdateInfoList = entities.Select(entityToUpdate =>
            EntityUpdateInfo<TEntity>.CreateUpdate(entityToUpdate.ToRtEntityId(), rtEntity)).ToList();

        await _bulkRtMutation.ApplyChangesAsync(session, _mongoDbRepositoryDataSource, entityUpdateInfoList,
            new Collection<AssociationUpdateInfo>());
    }

    public async Task DeleteOneAsync(IOctoSession session, CkId<CkTypeId> ckTypeId)
    {
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions == null)
        {
            throw TenantRepositoryException.NoFilterDefinitions();
        }

        var rtCollection = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(ckTypeId);
        var entities = await rtCollection.FindManyAsync(session, filterDefinitions);
        if (entities.Count != 1)
        {
            throw TenantRepositoryException.EntityFilterReturnNotExactlyOne();
        }

        var entityUpdateInfoList = entities.Select(entityToUpdate =>
            EntityUpdateInfo<TEntity>.CreateDelete(entityToUpdate.ToRtEntityId())).ToList();

        await _bulkRtMutation.ApplyChangesAsync(session, _mongoDbRepositoryDataSource, entityUpdateInfoList,
            new Collection<AssociationUpdateInfo>());
    }

    public async Task DeleteManyAsync(IOctoSession session, CkId<CkTypeId> ckTypeId)
    {
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions == null)
        {
            throw TenantRepositoryException.NoFilterDefinitions();
        }

        var rtCollection = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(ckTypeId);
        var entities = await rtCollection.FindManyAsync(session, filterDefinitions);

        var entityUpdateInfoList = entities.Select(entityToUpdate =>
            EntityUpdateInfo<TEntity>.CreateDelete(entityToUpdate.ToRtEntityId())).ToList();

        await _bulkRtMutation.ApplyChangesAsync(session, _mongoDbRepositoryDataSource, entityUpdateInfoList,
            new Collection<AssociationUpdateInfo>());
    }

    public async Task UpdateManyAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, TEntity rtEntity)
    {
        var filterDefinitions = CreateFilterDefinitions();
        if (filterDefinitions == null)
        {
            throw TenantRepositoryException.NoFilterDefinitions();
        }

        var rtCollection = _mongoDbRepositoryDataSource.GetRtDatabaseCollection<TEntity>(ckTypeId);
        var entities = await rtCollection.FindManyAsync(session, filterDefinitions);

        var entityUpdateInfoList = entities.Select(entityToUpdate =>
            EntityUpdateInfo<TEntity>.CreateUpdate(entityToUpdate.ToRtEntityId(), rtEntity)).ToList();

        await _bulkRtMutation.ApplyChangesAsync(session, _mongoDbRepositoryDataSource, entityUpdateInfoList,
            new Collection<AssociationUpdateInfo>());
    }

    protected override string ResolveAttributeName(string attributeName)
    {
        var baseResolve = base.ResolveAttributeName(attributeName);
        if (!string.IsNullOrEmpty(baseResolve))
        {
            return baseResolve;
        }

        if (typeof(RtEntity).GetProperty(attributeName) != null)
        {
            return attributeName.ToCamelCase();
        }

        return $"{Constants.AttributesName}.{attributeName.ToCamelCase()}";
    }
}