using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Meshmakers.Octo.Backend.Persistence.DataAccess.Internal;

namespace Meshmakers.Octo.Backend.Persistence.CkRuleEngine.Cache;

public interface ICkCache : IDisposable
{
    string TenantId { get; }

    bool IsDisposed { get; }

    IEnumerable<EntityCacheItem> GetCkEntities();

    EntityCacheItem GetEntityCacheItem(string ckId);

    Task Initialize(IDatabaseContext databaseContext);
}
