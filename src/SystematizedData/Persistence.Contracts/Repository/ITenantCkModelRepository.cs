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
    Task<ICollection<CkAttribute>> GetCkAttributesByScopeAsync(IOctoSession session, ScopeIds scopeId);
    Task<ICollection<CkEntity>> GetCkEntitiesByScopeAsync(IOctoSession session, ScopeIds scopeId);
    Task<ICollection<CkEntityInheritance>> GetCkEntityInheritancesByScopeAsync(IOctoSession session, ScopeIds scopeId);
    Task<IBulkImportResult> BulkImportCkAttributesAsync(IOctoSession session, IReadOnlyCollection<CkAttribute> ckAttributes);
    Task<IBulkImportResult> BulkImportCkEntitiesAsync(IOctoSession session, IReadOnlyCollection<CkEntity> ckEntities);
    Task<IBulkImportResult> BulkImportCkEntityAssociationsAsync(IOctoSession session, IReadOnlyCollection<CkEntityAssociation> ckEntityAssociations);
    Task<IBulkImportResult> BulkImportCkEntityInheritancesAsync(IOctoSession session, IReadOnlyCollection<CkEntityInheritance> ckEntityInheritances);
    Task DeleteCkEntityAssociationsOneAsync(IOctoSession session, object associationId);
    Task<ICollection<CkEntityAssociation>> GetCkEntityAssociationsByScopeAsync(IOctoSession session, ScopeIds scopeId);
    Task DeleteCkEntitiesOneAsync(IOctoSession session, string ckId);
    Task DeleteCkAttributesOneAsync(IOctoSession session, string attributeId);
    Task DeleteOneCkEntityInheritancesAsync(IOctoSession session, OctoObjectId inheritanceId);
    Task UpdateCollectionsAsync(IOctoSession session);
    Task UpdateIndexAsync(IOctoSession session);
}