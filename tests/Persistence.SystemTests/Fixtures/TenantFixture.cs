using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Configuration;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class TenantFixture : SystemFixture
{
    public async Task<ITenantRepositoryInternal> GetTenantRepositoryAsync()
    {
        var options = GetOptions<SystemTestOptions>("SystemTest");

        var systemContext = GetSystemContext();

        var tenantContext = await systemContext.CreateChildTenantContextInternalAsync(options.TenantId);
        
        return tenantContext.CreateOrGetTenantRepositoryInternal();
    }
}
