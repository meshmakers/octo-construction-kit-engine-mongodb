using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

namespace CkModel.CkRuleEngine;

public interface ICkCacheService
{
    Task InitializeAsync();
    Task<ICkCache> GetOrCreateCkCacheAsync(string tenantId, ITenantCkModelRepository tenantCkModelRepository);

    Task DistributeTenantModificationPreEventAsync(string tenantId);
    Task DistributeTenantModificationPostEventAsync(string tenantId);
}