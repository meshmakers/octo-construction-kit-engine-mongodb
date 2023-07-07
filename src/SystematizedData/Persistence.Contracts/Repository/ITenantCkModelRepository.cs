using System.Collections;
using System.Linq.Expressions;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public interface ITenantCkModelRepository
{
    Task<IOctoSession> StartSessionAsync();
    Task<ICollection<CkTypeInfo>> GetCkTypeInfoAsync(IOctoSession session);
    Task<ICkAttribute> FindSingleOrDefaultCkAttributesAsync(IOctoSession session, Expression<Func<CkAttribute, bool>> expression);
    Task<IBulkImportResult> BulkImportCkAttributesAsync(IOctoSession session, IReadOnlyCollection<CkAttribute> ckAttributes);
    Task<IBulkImportResult> BulkImportCkEntitiesAsync(IOctoSession session, IReadOnlyCollection<CkEntity> ckEntities);
    Task<IBulkImportResult> BulkImportCkEntityAssociationsAsync(IOctoSession session, IReadOnlyCollection<CkEntityAssociation> ckEntityAssociations);
    Task<IBulkImportResult> BulkImportCkEntityInheritancesAsync(IOctoSession session, IReadOnlyCollection<CkEntityInheritance> ckEntityInheritances);
    Task DeleteCkEntityAssociationsOneAsync(IOctoSession session, object associationId);
    Task DeleteCkEntitiesOneAsync(IOctoSession session, CkTypeId ckId);
    Task DeleteCkAttributesOneAsync(IOctoSession session, string attributeId);
    Task DeleteOneCkEntityInheritancesAsync(IOctoSession session, OctoObjectId inheritanceId);
    Task UpdateCollectionsAsync(IOctoSession session);
    Task UpdateIndexAsync(IOctoSession session);
    Task<IEnumerable<CkAttribute>> GetCkAttributesByModelAsync(IOctoSession session, CkModelId ckModelId);
    Task<IEnumerable<CkEntity>> GetCkEntitiesByModelAsync(IOctoSession session, CkModelId ckModelId);
    Task<IEnumerable<CkEntityAssociation>> GetCkEntityAssociationsByModelAsync(IOctoSession session, CkModelId ckModelId);
    Task<IEnumerable<CkEntityInheritance>> GetCkEntityInheritancesByModelAsync(IOctoSession session, CkModelId ckModelId);
}