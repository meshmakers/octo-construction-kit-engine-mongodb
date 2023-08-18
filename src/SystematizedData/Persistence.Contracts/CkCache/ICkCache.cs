using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public interface ICkCache : IDisposable
{
    string TenantId { get; }

    bool IsDisposed { get; }

    IEnumerable<IEntityCacheItem> GetCkEntities();

    IEntityCacheItem GetEntityCacheItem(CkId<CkTypeId> ckTypeId);

    void Unload();

    Task Initialize(ITenantCkModelRepository tenantCkModelRepository);
}
