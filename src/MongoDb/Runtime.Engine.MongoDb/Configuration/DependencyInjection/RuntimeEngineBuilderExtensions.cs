using Meshmakers.Octo.Backend.DistributedCache;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Runtime.Engine.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class RuntimeEngineBuilderExtensions
{
    public static IRuntimeEngineBuilder AddMongoDbRuntimeRepository(
        this IRuntimeEngineBuilder builder,
        Action<OctoSystemConfiguration>? setupSystemConfigurationAction = null)
    {
        if (setupSystemConfigurationAction != null) builder.Services.Configure(setupSystemConfigurationAction);

        // Adding dependent octo modules
        builder.Services.AddRuntimeEngine();
        builder.Services.AddDistributedPubSubCache();

        // Add basic construction kits. Hopefully we can leave it at one.
        builder.Services.AddCkModelSystem();

        // Add services of Persistence module
        builder.Services.AddTransient<ICkModelRepository, DatabaseCkModelRepository>();
        builder.Services.AddSingleton<ISystemContext, SystemContext>();
        builder.Services.AddSingleton<ISystemMessageService, SystemMessageService>();
        builder.Services.AddSingleton<IModelLoaderService, ModelLoaderService>();

        return builder;
    }
}