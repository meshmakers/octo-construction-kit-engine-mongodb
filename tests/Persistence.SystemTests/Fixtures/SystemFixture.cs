using Meshmakers.Octo.Runtime.Contracts.MongoDb;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class SystemFixture : DatabaseFixture
{
    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        // Initialize system tenant
        var systemContext = GetSystemContext();
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (i == 0 && await systemContext.IsSystemTenantExistingAsync())
                {
                    await systemContext.DeleteSystemTenantAsync();
                }

                if (await systemContext.IsSystemTenantExistingAsync())
                {
                    await Task.Delay(1000);
                    continue;
                }

                break;
            }
            catch (TenantException)
            {
                // do nothing here
            }
        }

        await systemContext.CreateSystemTenantAsync();
    }
}
