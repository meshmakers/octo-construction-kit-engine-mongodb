using System.Threading.Tasks;
using Meshmakers.Octo.Backend.Persistence.SystemTests.Configuration;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Meshmakers.Octo.Backend.Persistence.SystemTests;

public class TenantFixture : SystemFixture
{
    public async Task<ITenantRepositoryInternal> GetTenantRepositoryAsync()
    {
        var options = GetOptions<SystemTestOptions>("SystemTest");

        var systemContext = GetSystemContext();

        var tenantContext = await systemContext.CreateChildTenantContextInternalAsync(options.TenantId);
        
        return await tenantContext.CreateOrGetTenantRepositoryInternalAsync();
    }
}
