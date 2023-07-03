using System.Collections.Concurrent;
using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

namespace CkModel.CkRuleEngine;

public class CkCacheService : ICkCacheService
{
    private readonly IDistributedWithPubSubCache _distributedWithPubSubCache;
    private readonly ConcurrentDictionary<string, ICkCache?> _ckCaches;

    public CkCacheService(IDistributedWithPubSubCache distributedWithPubSubCache)
    {
        _distributedWithPubSubCache = distributedWithPubSubCache;
        _ckCaches = new ConcurrentDictionary<string, ICkCache?>();

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
        var ckCache = _ckCaches[key];
        if (ckCache != null)
        {
            ckCache.Unload();
        }
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task<ICkCache> GetOrCreateCkCacheAsync(string tenantId, ITenantCkModelRepository tenantCkModelRepository)
    {
        if (!_ckCaches.TryGetValue(tenantId.MakeKey(), out var ckCache))
        {
            ckCache = new CkCache(tenantId);
            await ckCache.Initialize(tenantCkModelRepository);

            var key = tenantId.MakeKey();
            _ckCaches[key] = ckCache;
            return ckCache;
        }

        return ckCache!;
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