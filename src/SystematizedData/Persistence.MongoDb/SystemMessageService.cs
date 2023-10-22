using System.Collections.Concurrent;
using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Persistence.InternalContracts;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class SystemMessageService : ISystemMessageService
{
    private readonly IDistributedWithPubSubCache _distributedWithPubSubCache;
    private readonly ICkCacheService _ckCacheService;

    public SystemMessageService(IDistributedWithPubSubCache distributedWithPubSubCache, ICkCacheService ckCacheService)
    {
        _distributedWithPubSubCache = distributedWithPubSubCache;
        _ckCacheService = ckCacheService;

        var sub = distributedWithPubSubCache.Subscribe<string>(CacheCommon.KeyTenantPreUpdate);
        sub.OnMessage(channelMessage =>
        {
            if (!string.IsNullOrWhiteSpace(channelMessage.Message))
            {
                UnloadCache(channelMessage.Message);
            }

            return Task.CompletedTask;
        });
    }
    
    private void UnloadCache(string tenantId)
    {
        var key = tenantId.MakeKey();

        if (_ckCacheService.IsTenantLoaded(key))
        {
            _ckCacheService.Unload(key);
        }
    }

    
    public async Task DistributeTenantModificationPreEventAsync(string tenantId)
    {
        await _distributedWithPubSubCache.PublishAsync(CacheCommon.KeyTenantPreUpdate, tenantId);
    }

    public async Task DistributeTenantModificationPostEventAsync(string tenantId)
    {
        await _distributedWithPubSubCache.PublishAsync(CacheCommon.KeyTenantPostUpdate, tenantId);
    }
}