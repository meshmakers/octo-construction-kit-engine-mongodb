using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

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

    public async Task DeleteCkEntityAssociationsOneAsync(IOctoSession session, object associationId)
    {
        await _databaseContext.CkEntityAssociations.DeleteOneAsync(session, associationId);
    }

    public async Task DeleteCkEntitiesOneAsync(IOctoSession session, CkTypeId ckId)
    {
        await _databaseContext.CkEntities.DeleteOneAsync(session, ckId);
    }

    public async Task DeleteCkAttributesOneAsync(IOctoSession session, string attributeId)
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

    public Task<IEnumerable<CkAttribute>> GetCkAttributesByModelAsync(IOctoSession session, CkModelId ckModelId)
    {
        _databaseContext.CkAttributes.FindManyAsync(session, x => x. == ckModelId)
    }

    public Task<IEnumerable<CkEntity>> GetCkEntitiesByModelAsync(IOctoSession session, CkModelId ckModelId)
    {
        throw new NotImplementedException();
    }

    public Task<IEnumerable<CkEntityAssociation>> GetCkEntityAssociationsByModelAsync(IOctoSession session, CkModelId ckModelId)
    {
        throw new NotImplementedException();
    }

    public Task<IEnumerable<CkEntityInheritance>> GetCkEntityInheritancesByModelAsync(IOctoSession session, CkModelId ckModelId)
    {
        throw new NotImplementedException();
    }
}