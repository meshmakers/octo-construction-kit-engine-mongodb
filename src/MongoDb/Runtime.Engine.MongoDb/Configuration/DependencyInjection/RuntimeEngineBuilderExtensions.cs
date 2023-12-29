using Meshmakers.Octo.Common.DistributionEventHub.Configuration;
using Meshmakers.Octo.Common.Shared.DistributionEventHub.Messages;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Runtime.Engine.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Consumers;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class RuntimeEngineBuilderExtensions
{
    public static IRuntimeEngineBuilder AddMongoDbRuntimeRepository(
        this IRuntimeEngineBuilder builder, string uniqueBrokerServiceAddress,
        Action<OctoSystemConfiguration>? setupSystemConfigurationAction = null, Action<IDistributionEventHubConfiguration>? configureDistributionEventHub = null)
    {
        if (setupSystemConfigurationAction != null)
        {
            builder.Services.Configure(setupSystemConfigurationAction);
        }

        // Adding dependent octo modules
        builder.Services.AddRuntimeEngine();
        builder.Services.AddDistributionEventHub(c =>
        {
            c.UniqueServiceAddress = uniqueBrokerServiceAddress;
        
            configureDistributionEventHub?.Invoke(c);
            
            c.AddBroadcastEventConsumer<PreUpdateTenantConsumer, PreUpdateTenant>();
        });
        // Add basic construction kits. Hopefully we can leave it at one.
        builder.Services.AddCkModelSystem();

        // Add services of Persistence module
        builder.Services.AddTransient<ICkModelRepository, DatabaseCkModelRepository>();
        builder.Services.AddSingleton<ISystemContext, SystemContext>();
        builder.Services.AddSingleton<IModelLoaderService, ModelLoaderService>();
        
        return builder;
    }
}