namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public interface ICkCache : IDisposable
{
    string TenantId { get; }

    bool IsDisposed { get; }

    IEnumerable<IEntityCacheItem> GetCkEntities();

    IEntityCacheItem GetEntityCacheItem(string ckId);

    void Unload();

    Task Initialize(ITenantCkModelRepository tenantCkModelRepository);
}
