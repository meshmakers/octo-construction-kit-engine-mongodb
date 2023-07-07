using System.Linq.Expressions;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public interface ITenantRepositoryInternal : ITenantRepository
{
    Task<CurrentMultiplicity> GetCurrentRtAssociationMultiplicityAsync(IOctoSession session, RtEntityId rtEntityId,
        string roleId, GraphDirections graphDirections);

    Task<AggregatedBulkImportResult>
        BulkInsertRtEntitiesAsync(IOctoSession session, IEnumerable<RtEntity> rtEntityList);

    Task<IBulkImportResult> BulkRtAssociationsAsync(IOctoSession session, IEnumerable<RtAssociation> rtAssociations);
    
    RtAssociation CreateTransientRtAssociation(RtEntityId originRtEntityId, string roleId,
        RtEntityId targetRtEntityId);

    Task InsertOneRtEntityAsync(IOctoSession session, CkTypeId ckId, RtEntity rtEntity);

    Task InsertOneRtEntityAsync<TEntity>(IOctoSession session, TEntity rtEntity)
        where TEntity : RtEntity, new();
    
    Task ReplaceOneRtEntityByIdAsync(IOctoSession session, CkTypeId ckId, OctoObjectId rtId, RtEntity rtEntity);

    Task ReplaceOneRtEntityByIdAsync<TEntity>(IOctoSession session, OctoObjectId rtId, TEntity rtEntity)
        where TEntity : RtEntity, new();

    Task DeleteOneRtEntityByRtIdAsync(IOctoSession session, CkTypeId ckId, OctoObjectId rtId);
    
    Task DeleteOneRtEntityByRtIdAsync<TEntity>(IOctoSession session, OctoObjectId rtId)
        where TEntity : RtEntity, new();

    Task DeleteOneRtEntityAsync(IOctoSession session, CkTypeId ckId, Expression<Func<RtEntity, bool>> filterExpression);
    
    Task DeleteOneRtEntityAsync<TEntity>(IOctoSession session, Expression<Func<TEntity, bool>> filterExpression)
        where TEntity : RtEntity, new();
}
