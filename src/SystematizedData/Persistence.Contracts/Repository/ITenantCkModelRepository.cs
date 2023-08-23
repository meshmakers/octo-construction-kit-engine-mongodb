using System.Linq.Expressions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public interface ITenantCkModelRepository
{
    Task<IOctoSession> StartSessionAsync();
    Task<ICollection<CkTypeInfo>> GetCkTypeInfoAsync(IOctoSession session);
    Task<CkAttribute> FindSingleOrDefaultCkAttributesAsync(IOctoSession session, Expression<Func<CkAttribute, bool>> expression);
    Task<IBulkImportResult> BulkImportCkAttributesAsync(IOctoSession session, IReadOnlyCollection<CkAttribute> ckAttributes);
    Task<IBulkImportResult> BulkImportCkEntitiesAsync(IOctoSession session, IReadOnlyCollection<CkEntity> ckEntities);
    Task<IBulkImportResult> BulkImportCkEntityAssociationsAsync(IOctoSession session, IReadOnlyCollection<CkEntityAssociation> ckEntityAssociations);
    Task<IBulkImportResult> BulkImportCkEntityInheritancesAsync(IOctoSession session, IReadOnlyCollection<CkEntityInheritance> ckEntityInheritances);
    Task DeleteCkEntityAssociationsOneAsync(IOctoSession session, OctoObjectId associationId);
    Task DeleteCkEntitiesOneAsync(IOctoSession session,  CkId<CkTypeId> ckId);
    Task DeleteCkAttributesOneAsync(IOctoSession session, CkId<CkAttributeId> attributeId);
    Task DeleteOneCkEntityInheritancesAsync(IOctoSession session, OctoObjectId inheritanceId);
    Task UpdateCollectionsAsync(IOctoSession session);
    Task UpdateIndexAsync(IOctoSession session);
    Task<IEnumerable<CkAttribute>> GetCkAttributesByModelAsync(IOctoSession session, CkModelId ckModelId);
    Task<IEnumerable<CkEntity>> GetCkEntitiesByModelAsync(IOctoSession session, CkModelId ckModelId);
    Task<IEnumerable<CkEntityAssociation>> GetCkEntityAssociationsByModelAsync(IOctoSession session, CkModelId ckModelId);
    Task<IEnumerable<CkEntityInheritance>> GetCkEntityInheritancesByModelAsync(IOctoSession session, CkModelId ckModelId);
    Task<IEnumerable<CkAssociationRole>> GetCkAssociationRolesByModelAsync(IOctoSession session, CkModelId ckModelId);
    Task<CkAssociationRole?> GetCkAssociationRoleAsync(IOctoSession session, CkId<CkAssociationRoleId> ckAssociationId);
    Task DeleteCkAssociationRoleOneAsync(IOctoSession session, CkId<CkAssociationRoleId> associationRoleId);
    Task<IBulkImportResult> BulkImportCkAssociationRoleAsync(IOctoSession session, IReadOnlyCollection<CkAssociationRole> ckAssociationRoles);
    Task<bool> IsCkModelExistingAsync(IOctoSession session, CkModelId ckModelId);
    Task DeleteCkModelOneAsync(IOctoSession session, CkModelId ckModelId);
    Task InsertCkModelAsync(IOctoSession session, DatabaseEntities.CkModel ckModel);
}