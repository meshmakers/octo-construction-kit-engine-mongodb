using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

public class SystemMessageService : ISystemMessageService
{
    private readonly ICkCacheService _ckCacheService;
    private readonly IDistributedWithPubSubCache _distributedWithPubSubCache;

    public SystemMessageService(IDistributedWithPubSubCache distributedWithPubSubCache, ICkCacheService ckCacheService)
    {
        _distributedWithPubSubCache = distributedWithPubSubCache;
        _ckCacheService = ckCacheService;

        var sub = distributedWithPubSubCache.Subscribe<string>(CacheCommon.KeyTenantPreUpdate);
        sub.OnMessage(channelMessage =>
        {
            if (!string.IsNullOrWhiteSpace(channelMessage.Message)) UnloadCache(channelMessage.Message);

            return Task.CompletedTask;
        });
    }


    public async Task DistributeTenantModificationPreEventAsync(string tenantId)
    {
        await _distributedWithPubSubCache.PublishAsync(CacheCommon.KeyTenantPreUpdate, tenantId);
    }

    public async Task DistributeTenantModificationPostEventAsync(string tenantId)
    {
        await _distributedWithPubSubCache.PublishAsync(CacheCommon.KeyTenantPostUpdate, tenantId);
    }

    private void UnloadCache(string tenantId)
    {
        var key = tenantId.MakeKey();

        if (_ckCacheService.IsTenantLoaded(key)) _ckCacheService.Unload(key);
    }
}