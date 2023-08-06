using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkModelEntities;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.InsertModifiers;

public class AutoIncrementModifier : IAutoIncrementModifier
{
    private readonly IDatabaseContext _databaseContext;
    private readonly ICkCache _ckCache;
    private readonly ITenantRepository _tenantRepository;

    public AutoIncrementModifier(IDatabaseContext databaseContext, ICkCache ckCache, ITenantRepository tenantRepository)
    {
        _databaseContext = databaseContext;
        _ckCache = ckCache;
        _tenantRepository = tenantRepository;
    }
    
    public async Task RunAutoIncrementAsync(IOctoSession session, CkId<CkTypeId> ckId, IEnumerable<RtEntity> rtEntities)
    {
        var entityCacheItem = _ckCache.GetEntityCacheItem(ckId);
        if (entityCacheItem == null)
        {
            throw new InvalidCkIdException($"Construction Kit Id '{ckId}' is invalid.");
        }

        var autoIncrementReferences = entityCacheItem.Attributes.Values
            .Where(a => !string.IsNullOrEmpty(a.AutoIncrementReference)).ToList();
        if (!autoIncrementReferences.Any())
        {
            return;
        }

        var dataQueryOperation = new DataQueryOperation
        {
            FieldFilters = new[]
            {
                new FieldFilter(nameof(RtEntity.RtWellKnownName), FieldFilterOperator.In,
                    autoIncrementReferences.Select(x => x.AutoIncrementReference))
            }
        };

        var autoIncrementerSet = await _tenantRepository.GetRtEntitiesByTypeAsync<RtSystemAutoIncrement>(session, dataQueryOperation);

        foreach (var rtEntity in rtEntities)
        foreach (var autoIncrementReference in autoIncrementReferences)
        {
            var attributeCacheItem = entityCacheItem.Attributes[autoIncrementReference.AttributeName];
            if (attributeCacheItem == null)
            {
                throw new InvalidAttributeException(
                    $"Attribute with name '{autoIncrementReference.AttributeName}' does not exist at Ck-Id {ckId}");
            }

            var autoIncrement = autoIncrementerSet.Items.FirstOrDefault(x =>
                x.RtWellKnownName == autoIncrementReference.AutoIncrementReference);
            if (autoIncrement == null)
            {
                throw new InvalidAttributeException(
                    $"Autoincrement reference '{autoIncrementReference.AutoIncrementReference}' does not exist at Ck-Id {ckId}");
            }
            rtEntity.SetAttributeValue(autoIncrementReference.AttributeName,
                attributeCacheItem.AttributeValueType,
                await ExecuteAutoIncrementAsync(session, autoIncrement));
        }
    }

    public async Task<long> ExecuteAutoIncrementAsync(IOctoSession session, RtSystemAutoIncrement autoIncrement)
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
        await _databaseContext.GetRtCollection<RtEntity>(autoIncrement.CkId)
            .ReplaceByIdAsync(session, autoIncrement.RtId, autoIncrement);

        return currentValue;
    }
}