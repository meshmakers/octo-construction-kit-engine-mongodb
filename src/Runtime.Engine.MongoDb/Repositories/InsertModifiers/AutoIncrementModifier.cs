using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.InsertModifiers;

public class AutoIncrementModifier : IAutoIncrementModifier
{
    private readonly ICkCacheService _ckCacheService;
    private readonly IMongoDbRepositoryDataSource _mongoDbRepositoryDataSource;
    private readonly ITenantRepository _tenantRepository;

    public AutoIncrementModifier(IMongoDbRepositoryDataSource mongoDbRepositoryDataSource, ICkCacheService ckCacheService,
        ITenantRepository tenantRepository)
    {
        _mongoDbRepositoryDataSource = mongoDbRepositoryDataSource;
        _ckCacheService = ckCacheService;
        _tenantRepository = tenantRepository;
    }

    public async Task RunAutoIncrementAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, IEnumerable<RtEntity> rtEntities)
    {
        var entityCacheItem = _ckCacheService.GetCkType(_tenantRepository.TenantId, ckTypeId);
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
            throw AutoIncrementFailedException.InvalidNullCurrentValue(autoIncrement.RtId);
        }

        var currentValue = autoIncrement.CurrentValue.Value;

        currentValue++;

        if (currentValue > end)
        {
            throw AutoIncrementFailedException.AutoIncrementEndReached(autoIncrement.RtId);
        }
        
        var ckTypeGraph = _ckCacheService.GetCkType(_tenantRepository.TenantId, autoIncrement.CkTypeId ?? throw OperationFailedException.CkTypeIdUndefined());

        autoIncrement.CurrentValue = currentValue;
        await _mongoDbRepositoryDataSource.GetRtCollection<RtEntity>(ckTypeGraph)
            .ReplaceByIdAsync(session, autoIncrement.RtId, autoIncrement);

        return currentValue;
    }
}