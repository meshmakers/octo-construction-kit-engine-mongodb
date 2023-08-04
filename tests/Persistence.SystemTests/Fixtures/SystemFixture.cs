using CkModel.CkRuleEngine;
using FakeItEasy;
using Meshmakers.Octo.Backend.Persistence.SystemTests.Configuration;
using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine;
using Meshmakers.Octo.SystematizedData.Persistence.Commands;
using Microsoft.Extensions.Options;
using Persistence.InternalContracts;
using Persistence.SystemCkModel;

namespace Meshmakers.Octo.Backend.Persistence.SystemTests;

public class SystemFixture : ConfigurationFixture
{
    public ISystemContextInternal GetSystemContext()
    {
        var distributedWithPubSubCache = A.Fake<IDistributedWithPubSubCache>();
        var systemModelService = new CkSystemModelService(new ImportCkModelCommand());
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
}