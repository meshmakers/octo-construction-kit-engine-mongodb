using FakeItEasy;
using Meshmakers.Octo.Backend.Persistence.SystemTests.CkTest;
using Meshmakers.Octo.Backend.Persistence.SystemTests.Configuration;
using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine;
using Meshmakers.Octo.SystematizedData.Persistence.Commands;
using Microsoft.Extensions.Options;
using Persistence.Commands;
using Persistence.InternalContracts;
using Persistence.SystemCkModel;

namespace Meshmakers.Octo.Backend.Persistence.SystemTests.Fixtures;

public class SystemFixture : ConfigurationFixture, IDisposable
{
    public SystemFixture()
    {
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
    
    public ISystemContextInternal GetSystemContext()
    {
        var distributedWithPubSubCache = A.Fake<IDistributedWithPubSubCache>();
        var systemModelService = new CkTestModelService(new ImportCkModelCommand(new CkJsonSerializer()));
        var cacheService = new CkCacheService(distributedWithPubSubCache);
        
        var options = GetOptions<SystemTestOptions>("SystemTest");

        var systemContext = new SystemContext(new OptionsWrapper<OctoSystemConfiguration>(
            new OctoSystemConfiguration
            {
                AdminUserPassword = options.AdminUserPassword,
                DatabaseUserPassword = options.DatabaseUserPassword
            }), cacheService, systemModelService);

        return systemContext;
    }
    
    public void Dispose()
    {
      //  var systemContext = GetSystemContext();
       // Task.WaitAll(systemContext.DeleteSystemTenantAsync());
    }
}