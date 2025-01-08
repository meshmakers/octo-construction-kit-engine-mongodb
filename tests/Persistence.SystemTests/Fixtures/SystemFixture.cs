using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

public class SystemFixture : ConfigurationFixture, IDisposable
{
    private readonly SystemTestOptions _options;
    // ReSharper disable once MemberCanBeProtected.Global
    public SystemFixture()
    {
        _options ??= GetOptions<SystemTestOptions>("systemTest");
        Services.Configure<OctoSystemConfiguration>(t =>
        {
            t.SystemDatabaseName = "PersistenceSystemTests";
            t.AdminUserPassword = _options.AdminUserPassword;
            t.DatabaseUserPassword = _options.DatabaseUserPassword;
            t.UseDirectConnection = _options.UseDirectConnection;
        });

        Provider = Services.BuildServiceProvider();

        Task.WaitAll(Task.Run(async () =>
        {
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
                        Thread.Sleep(1000);
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
        }));
    }

    public string TestTenantId => _options.TenantId;

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