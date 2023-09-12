using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Persistence.InternalContracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class SystemFixture : ConfigurationFixture, IDisposable
{
    public SystemFixture()
    {
        Services.Configure<OctoSystemConfiguration>(t =>
        {
            var options = base.GetOptions<SystemTestOptions>("systemTest");
            t.AdminUserPassword = options.AdminUserPassword;
            t.DatabaseUserPassword = options.DatabaseUserPassword;
        });
        
        Provider = Services.BuildServiceProvider();

        Task.WaitAll(Task.Run(async () =>
        {
            var systemContext = GetSystemContext();
            if (await systemContext.IsSystemTenantExistingAsync())
            {
                await systemContext.DeleteSystemTenantAsync();
            }

            await systemContext.CreateSystemTenantAsync();
        }));
    }

    public ServiceProvider Provider { get; private set; }

    public ISystemContextInternal GetSystemContext()
    {

        return Provider.GetRequiredService<ISystemContextInternal>();
            //
            // var systemContext = new SystemContext(new OptionsWrapper<OctoSystemConfiguration>(
            //     new OctoSystemConfiguration
            //     {
            //         AdminUserPassword = options.AdminUserPassword,
            //         DatabaseUserPassword = options.DatabaseUserPassword
            //     }), cacheService, systemModelService);
            //
            // return systemContext;
    }

    public void Dispose()
    {
        //  var systemContext = GetSystemContext();
        // Task.WaitAll(systemContext.DeleteSystemTenantAsync());
    }
}