using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class SystemFixture : ConfigurationFixture, IDisposable
{
    public SystemFixture()
    {
        Services.Configure<OctoSystemConfiguration>(t =>
        {
            var options = GetOptions<SystemTestOptions>("systemTest");
            t.AdminUserPassword = options.AdminUserPassword;
            t.DatabaseUserPassword = options.DatabaseUserPassword;
        });

        Provider = Services.BuildServiceProvider();

        // Task.WaitAll(Task.Run(async () =>
        // {
        //     var systemContext = GetSystemContext();
        //     if (await systemContext.IsSystemTenantExistingAsync())
        //     {
        //         await systemContext.DeleteSystemTenantAsync();
        //     }
        //
        //     await systemContext.CreateSystemTenantAsync();
        // }));
    }

    public ServiceProvider Provider { get; }

    public void Dispose()
    {
        //  var systemContext = GetSystemContext();
        // Task.WaitAll(systemContext.DeleteSystemTenantAsync());
    }

    public ISystemContext GetSystemContext()
    {
        return Provider.GetRequiredService<ISystemContext>();
    }
}