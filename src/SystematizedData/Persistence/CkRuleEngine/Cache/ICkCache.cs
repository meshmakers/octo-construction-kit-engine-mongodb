using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public interface ICkCache : IDisposable
{
    string TenantId { get; }

    bool IsDisposed { get; }

    IEnumerable<EntityCacheItem> GetCkEntities();

    EntityCacheItem GetEntityCacheItem(string ckId);

    Task Initialize(IDatabaseContext databaseContext);
}
