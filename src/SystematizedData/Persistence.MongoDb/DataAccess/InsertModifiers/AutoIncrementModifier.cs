using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Persistence.SystemCkModel.ConstructionKit.Generated.System.v1;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.InsertModifiers;

public class AutoIncrementModifier : IAutoIncrementModifier
{
    private readonly IDatabaseContext _databaseContext;
    private readonly ICkCacheService _ckCache;
    private readonly ITenantRepository _tenantRepository;

    public AutoIncrementModifier(IDatabaseContext databaseContext, ICkCacheService ckCache, ITenantRepository tenantRepository)
    {
        _databaseContext = databaseContext;
        _ckCache = ckCache;
        _tenantRepository = tenantRepository;
    }
    
    public async Task RunAutoIncrementAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, IEnumerable<RtEntity> rtEntities)
    {
        var entityCacheItem = _ckCache.GetCkType(_tenantRepository.TenantId, ckTypeId);
        if (entityCacheItem == null)
        {
            throw new InvalidCkTypeIdException($"Construction Kit Type Id '{ckTypeId}' is invalid.");
        }

        var autoIncrementReferences = entityCacheItem.AllAttributes.Values
            .Where(a => !string.IsNullOrEmpty(a.AutoIncrementReference)).ToList();
        if (!autoIncrementReferences.Any())
        {
            return;
        }

        var dataQueryOperation = DataQueryOperation.Create()
            .FieldFilter(nameof(RtEntity.RtWellKnownName), FieldFilterOperator.In,
                autoIncrementReferences.Select(x => x.AutoIncrementReference));

        var autoIncrementerSet = await _tenantRepository.GetRtEntitiesByTypeAsync<RtAutoIncrement>(session, dataQueryOperation);

        foreach (var rtEntity in rtEntities)
        foreach (var autoIncrementReference in autoIncrementReferences)
        {
            var attributeCacheItem = entityCacheItem.AllAttributes[autoIncrementReference.AttributeName];
            if (attributeCacheItem == null)
            {
                throw new InvalidAttributeException(
                    $"Attribute with name '{autoIncrementReference.AttributeName}' does not exist at Ck-Id {ckTypeId}");
            }

            var autoIncrement = autoIncrementerSet.Items.FirstOrDefault(x =>
                x.RtWellKnownName == autoIncrementReference.AutoIncrementReference);
            if (autoIncrement == null)
            {
                throw new InvalidAttributeException(
                    $"Autoincrement reference '{autoIncrementReference.AutoIncrementReference}' does not exist at Ck-Id {ckTypeId}");
            }
            rtEntity.SetAttributeValue(autoIncrementReference.AttributeName,
                attributeCacheItem.ValueType,
                await ExecuteAutoIncrementAsync(session, autoIncrement));
        }
    }

    public async Task<long> ExecuteAutoIncrementAsync(IOctoSession session, RtAutoIncrement autoIncrement)
    {
        var end = autoIncrement.End;
        if (!autoIncrement.CurrentValue.HasValue)
        {
            throw new AutoIncrementFailedException(
                $"'{autoIncrement.RtId}' cannot be incremented because current value was null.");
        }

        var currentValue = autoIncrement.CurrentValue.Value;

        currentValue++;

        if (currentValue > end)
        {
            throw new AutoIncrementFailedException(
                $"'{autoIncrement.RtId}' cannot be incremented because end value is reached.");
        }

        autoIncrement.CurrentValue = currentValue;
        await _databaseContext.GetRtCollection<RtEntity>(autoIncrement.CkTypeId)
            .ReplaceByIdAsync(session, autoIncrement.RtId, autoIncrement);

        return currentValue;
    }
}