using Meshmakers.Octo.Backend.DistributedCache;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Persistence.InternalContracts;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOctoMongoDbPersistence(
        this IServiceCollection services,
        Action<OctoSystemConfiguration>? setupSystemConfigurationAction = null)
    {
        if (setupSystemConfigurationAction != null)
        {
            services.Configure(setupSystemConfigurationAction);
        }

        // Adding dependent octo modules
        services.AddRuntimeEngine();
        services.AddDistributedPubSubCache();
        
        // Add basic construction kits. Hopefully we can leave it at one.
        services.AddCkModelSystem();
        
        // Add services of Persistence module
        services.AddTransient<ICkModelRepository, DatabaseCkModelRepository>();
        services.AddSingleton<ISystemContext, SystemContext>();
        services.AddSingleton<ISystemMessageService, SystemMessageService>();
        services.AddSingleton<IModelLoaderService, ModelLoaderService>();

        return services;
    }
}
