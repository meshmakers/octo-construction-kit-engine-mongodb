using System.Linq.Expressions;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public interface ITenantRepositoryInternal : ITenantRepository
{
    Task<CurrentMultiplicity> GetCurrentRtAssociationMultiplicityAsync(IOctoSession session, RtEntityId rtEntityId,
        CkId<CkAssociationId> roleId, GraphDirections graphDirections);

    Task<AggregatedBulkImportResult>
        BulkInsertRtEntitiesAsync(IOctoSession session, IEnumerable<RtEntity> rtEntityList);

    Task<IBulkImportResult> BulkRtAssociationsAsync(IOctoSession session, IEnumerable<RtAssociation> rtAssociations);
    
    RtAssociation CreateTransientRtAssociation(RtEntityId originRtEntityId,  CkId<CkAssociationId> roleId,
        RtEntityId targetRtEntityId);

    Task InsertOneRtEntityAsync(IOctoSession session, CkId<CkTypeId> ckId, RtEntity rtEntity);

    Task InsertOneRtEntityAsync<TEntity>(IOctoSession session, TEntity rtEntity)
        where TEntity : RtEntity, new();
    
    Task ReplaceOneRtEntityByIdAsync(IOctoSession session, CkId<CkTypeId> ckId, OctoObjectId rtId, RtEntity rtEntity);

    Task ReplaceOneRtEntityByIdAsync<TEntity>(IOctoSession session, OctoObjectId rtId, TEntity rtEntity)
        where TEntity : RtEntity, new();

    Task DeleteOneRtEntityByRtIdAsync(IOctoSession session, CkId<CkTypeId> ckId, OctoObjectId rtId);
    
    Task DeleteOneRtEntityByRtIdAsync<TEntity>(IOctoSession session, OctoObjectId rtId)
        where TEntity : RtEntity, new();

    Task DeleteOneRtEntityAsync(IOctoSession session, CkId<CkTypeId> ckId, Expression<Func<RtEntity, bool>> filterExpression);
    
    Task DeleteOneRtEntityAsync<TEntity>(IOctoSession session, Expression<Func<TEntity, bool>> filterExpression)
        where TEntity : RtEntity, new();
}
