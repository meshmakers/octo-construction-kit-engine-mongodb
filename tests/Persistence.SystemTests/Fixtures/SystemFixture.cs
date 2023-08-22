using FakeItEasy;
using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.ModelRepositories;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Resolvers;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Validation;
using Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine;
using Meshmakers.Octo.SystematizedData.Persistence.Commands;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.CkTest;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Persistence.InternalContracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

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

    public CkModelValidator CkModelValidator { get; private set; } = null!;

    public ISystemContextInternal GetSystemContext()
    {
        var logger = A.Fake<ILogger<ImportCkModelCommand>>();
        var logger2 = A.Fake<ILogger<DependencyResolver>>();
        var logger3 = A.Fake<ILogger<InheritanceResolver>>();
        var distributedWithPubSubCache = A.Fake<IDistributedWithPubSubCache>();
        var ckModelRepositoryManager = new CkModelRepositoryManager();
        CkModelValidator = new CkModelValidator(new DependencyResolver(logger2, ckModelRepositoryManager),
            new InheritanceResolver(logger3), new ElementResolver());
        var systemModelService = new CkTestModelService(new ImportCkModelCommand(logger, new CkJsonSerializer(), CkModelValidator));
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