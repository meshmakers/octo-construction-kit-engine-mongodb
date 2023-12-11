using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

public class SystemMessageService : ISystemMessageService
{
    private readonly ICkCacheService _ckCacheService;
    private readonly IDistributedCache _distributedCache;

    public SystemMessageService(IDistributedCache distributedCache, ICkCacheService ckCacheService)
    {
        _distributedCache = distributedCache;
        _ckCacheService = ckCacheService;
    }


    public async Task DistributeTenantModificationPreEventAsync(string tenantId)
    {
        await _distributedCache.TriggerEventAsync(CacheCommon.KeyTenantPreUpdate, tenantId);
    }

    public async Task DistributeTenantModificationPostEventAsync(string tenantId)
    {
        await _distributedCache.TriggerEventAsync(CacheCommon.KeyTenantPostUpdate, tenantId);
    }

    private void UnloadCache(string tenantId)
    {
        var key = tenantId.MakeKey();

        if (_ckCacheService.IsTenantLoaded(key))
        {
            _ckCacheService.Unload(key);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var sub = await _distributedCache.SubscribeEventAsync<string>(CacheCommon.KeyTenantPreUpdate);
        sub.OnEvent(tenantId =>
        {
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                UnloadCache(tenantId);
            }

            return Task.CompletedTask;
        });
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}