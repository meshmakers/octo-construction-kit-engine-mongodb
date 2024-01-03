using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.ConstructionKit.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.InsertModifiers;

public class AutoIncrementModifier : IAutoIncrementModifier
{
    private readonly ICkCacheService _ckCache;
    private readonly IMongoDbRepositoryDataSource _mongoDbRepositoryDataSource;
    private readonly ITenantRepository _tenantRepository;

    public AutoIncrementModifier(IMongoDbRepositoryDataSource mongoDbRepositoryDataSource, ICkCacheService ckCache,
        ITenantRepository tenantRepository)
    {
        _mongoDbRepositoryDataSource = mongoDbRepositoryDataSource;
        _ckCache = ckCache;
        _tenantRepository = tenantRepository;
    }

    public async Task RunAutoIncrementAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, IEnumerable<RtEntity> rtEntities)
    {
        var entityCacheItem = _ckCache.GetCkType(_tenantRepository.TenantId, ckTypeId);
        if (entityCacheItem == null)
        {
            throw InvalidCkTypeIdException.CkTypeIdNotFound(_tenantRepository.TenantId, ckTypeId);
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
                throw InvalidAttributeException.AttributeNotFound(ckTypeId, autoIncrementReference.AttributeName);
            }

            var autoIncrement = autoIncrementerSet.Items.FirstOrDefault(x =>
                x.RtWellKnownName == autoIncrementReference.AutoIncrementReference);
            if (autoIncrement == null)
            {
                throw InvalidAttributeException.AutoIncrementReferenceNotFound(ckTypeId,
                    autoIncrementReference.AutoIncrementReference);
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
        await _mongoDbRepositoryDataSource.GetRtCollection<RtEntity>(autoIncrement.CkTypeId)
            .ReplaceByIdAsync(session, autoIncrement.RtId, autoIncrement);

        return currentValue;
    }
}