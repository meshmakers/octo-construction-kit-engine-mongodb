using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class TenantCkModelRepository : ITenantCkModelRepository
{
    private readonly IDatabaseContext _databaseContext;

    public TenantCkModelRepository(IDatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }
    
    public async Task<IOctoSession> StartSessionAsync()
    {
        return await _databaseContext.StartSessionAsync();
    }

    public async Task<ICollection<CkTypeInfo>> GetCkTypeInfoAsync(IOctoSession session)
    {
        return await _databaseContext.GetCkTypeInfoAsync(session);
    }

    public async Task<ICkAttribute> FindSingleOrDefaultCkAttributesAsync(IOctoSession session, Expression<Func<CkAttribute, bool>> expression)
    {
        return await _databaseContext.CkAttributes.FindSingleOrDefaultAsync(session, expression);
    }



    public async Task<IBulkImportResult> BulkImportCkAttributesAsync(IOctoSession session, IReadOnlyCollection<CkAttribute> ckAttributes)
    {
        return await _databaseContext.CkAttributes.BulkImportAsync(session, ckAttributes);
    }

    public async Task<IBulkImportResult> BulkImportCkEntitiesAsync(IOctoSession session, IReadOnlyCollection<CkEntity> ckEntities)
    {
        return await _databaseContext.CkEntities.BulkImportAsync(session, ckEntities);
    }

    public async Task<IBulkImportResult> BulkImportCkEntityAssociationsAsync(IOctoSession session, IReadOnlyCollection<CkEntityAssociation> ckEntityAssociations)
    {
        return await _databaseContext.CkEntityAssociations.BulkImportAsync(session, ckEntityAssociations);
    }

    public async Task<IBulkImportResult> BulkImportCkEntityInheritancesAsync(IOctoSession session, IReadOnlyCollection<CkEntityInheritance> ckEntityInheritances)
    {
        return await _databaseContext.CkEntityInheritances.BulkImportAsync(session, ckEntityInheritances);
    }

    public async Task DeleteCkEntityAssociationsOneAsync(IOctoSession session, OctoObjectId associationId)
    {
        await _databaseContext.CkEntityAssociations.DeleteOneAsync(session, associationId);
    }

    public async Task DeleteCkEntitiesOneAsync(IOctoSession session, CkId<CkTypeId> ckId)
    {
        await _databaseContext.CkEntities.DeleteOneAsync(session, ckId);
    }

    public async Task DeleteCkAttributesOneAsync(IOctoSession session, CkId<CkAttributeId> attributeId)
    {
        await _databaseContext.CkAttributes.DeleteOneAsync(session, attributeId);
    }

    public async Task DeleteOneCkEntityInheritancesAsync(IOctoSession session, OctoObjectId inheritanceId)
    {
        await _databaseContext.CkEntityInheritances.DeleteOneAsync(session, inheritanceId);
    }

    public async Task UpdateCollectionsAsync(IOctoSession session)
    {
        await _databaseContext.UpdateCollectionsAsync(session);
    }

    public async Task UpdateIndexAsync(IOctoSession session)
    {
        await _databaseContext.UpdateIndexAsync(session);
    }

    public async Task<IEnumerable<CkAttribute>> GetCkAttributesByModelAsync(IOctoSession session, CkModelId ckModelId)
    {
        return await _databaseContext.CkAttributes.FindManyAsync(session, x => x.AttributeId.ModelId == ckModelId.ModelId);
    }

    public async Task<IEnumerable<CkEntity>> GetCkEntitiesByModelAsync(IOctoSession session, CkModelId ckModelId)
    {
        return await _databaseContext.CkEntities.FindManyAsync(session, x => x.CkId.ModelId == ckModelId.ModelId);
    }

    public async Task<IEnumerable<CkEntityAssociation>> GetCkEntityAssociationsByModelAsync(IOctoSession session, CkModelId ckModelId)
    {
        return await _databaseContext.CkEntityAssociations.FindManyAsync(session, x => x.RoleId.ModelId == ckModelId.ModelId);
    }

    public async Task<IEnumerable<CkEntityInheritance>> GetCkEntityInheritancesByModelAsync(IOctoSession session, CkModelId ckModelId)
    {
        return await _databaseContext.CkEntityInheritances.FindManyAsync(session, x => x.TargetCkId.ModelId == ckModelId.ModelId);
    }

    public async Task<IEnumerable<CkAssociationRole>> GetCkAssociationRolesByModelAsync(IOctoSession session, CkModelId ckModelId)
    {
        return await _databaseContext.CkAssociationRoles.FindManyAsync(session, x => x.RoleId.ModelId == ckModelId.ModelId);
    }

    public async Task<CkAssociationRole?> GetCkAssociationRoleAsync(IOctoSession session, CkId<CkAssociationId> ckAssociationId)
    {
        return await _databaseContext.CkAssociationRoles.DocumentAsync(session, ckAssociationId);
    }

    public async Task DeleteCkAssociationRoleOneAsync(IOctoSession session, CkId<CkAssociationId> associationRoleId)
    {
        await _databaseContext.CkAssociationRoles.DeleteOneAsync(session, associationRoleId);
    }

    public async Task<IBulkImportResult> BulkImportCkAssociationRoleAsync(IOctoSession session, IReadOnlyCollection<CkAssociationRole> ckAssociationRoles)
    {
        return await _databaseContext.CkAssociationRoles.BulkImportAsync(session, ckAssociationRoles);
    }

    public async Task<bool> IsCkModelExistingAsync(IOctoSession session, CkModelId ckModelId)
    {
        return (await _databaseContext.CkModels.DocumentAsync(session, ckModelId) != null);
    }

    public async Task DeleteCkModelOneAsync(IOctoSession session, CkModelId ckModelId)
    {
        //await _databaseContext.CkModels.DeleteOneAsync(session, ckModelId);
    }

    public async Task InsertCkModelAsync(IOctoSession session, DatabaseEntities.CkModel ckModel)
    {
        await _databaseContext.CkModels.InsertAsync(session, ckModel);
    }
}