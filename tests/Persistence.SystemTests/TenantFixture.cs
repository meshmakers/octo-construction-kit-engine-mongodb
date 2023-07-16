using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.Backend.Persistence.SystemTests.Configuration;
using Meshmakers.Octo.Common.Shared.DistributedCache;
using Meshmakers.Octo.SystematizedData.Persistence;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Persistence.SystemTests;

public class TenantFixture : SystemTestFixture
{
    public async Task<ITenantContext> GetTenantContextAsync()
    {
        var distributedWithPubSubCache = A.Fake<IDistributedWithPubSubCache>();

        var options = GetOptions<SystemTestOptions>("SystemTest");

        var systemContext = new SystemContext(new OptionsWrapper<OctoSystemConfiguration>(
            new OctoSystemConfiguration
            {
                AdminUserPassword = options.AdminUserPassword,
                DatabaseUserPassword = options.DatabaseUserPassword
            }), distributedWithPubSubCache);

        return await systemContext.CreateOrGetTenantContextAsync(options.TenantId);
    }
}
