using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Runtime.Engine.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Comparators;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Loaders;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Defaults;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Service collection extensions for the MongoDb runtime repository
/// </summary>
public static class RuntimeEngineBuilderExtensions
{
    /// <summary>
    ///     Add the MongoDb runtime repository to the runtime engine builder
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="setupSystemConfigurationAction"></param>
    /// <returns></returns>
    public static IRuntimeEngineBuilder AddMongoDbRuntimeRepository(
        this IRuntimeEngineBuilder builder,
        Action<OctoSystemConfiguration>? setupSystemConfigurationAction = null)
    {
        if (setupSystemConfigurationAction != null)
        {
            builder.Services.Configure(setupSystemConfigurationAction);
        }

        // Add basic construction kits. Hopefully we can leave it at one.
        builder.Services.AddCkModelSystem();

        // Add services of Persistence module
        builder.Services.AddTransient<ICkModelRepository, DatabaseCkModelRepository>();
        builder.Services.AddSingleton<ISystemContext, SystemContext>();
        builder.Services.AddSingleton<IModelLoaderService, ModelLoaderService>();
        builder.Services.AddSingleton<IMetricsContext, MetricsContext>();
        builder.Services.TryAddSingleton<ITenantNotifications, DefaultTenantNotifications>();
        builder.Services.AddTransient<IRepositoryOpsService, RepositoryOpsService>();
        builder.Services.AddTransient<ITenantBackupService, TenantBackupService>();

        builder.Services.AddSingleton<IUserRepositoryAccess, UserRepositoryAccess>();
        builder.Services.AddSingleton<IAdminRepositoryAccess, AdminRepositoryAccess>();

        // Add pre-document modification services
        builder.Services.AddTransient<IPreDocumentModification<RtEntity>, AutoIncrementModifier>();


        MongoRepositoryClient.RegisterSerializers();
        
        return builder;
    }
    
    /// <summary>
    ///     Add the Tenant Comparison feature to the runtime engine builder
    /// </summary>
    /// <param name="builder">Runtime engine builder</param>
    /// <returns>The builder for chaining</returns>
    public static IRuntimeEngineBuilder AddTenantComparison(
        this IRuntimeEngineBuilder builder)
    {
        // Register comparison loaders
        builder.Services.AddTransient<MetadataLoader>();
        builder.Services.AddTransient<CkModelLoader>();

        // Register comparison comparators
        builder.Services.AddTransient<MetadataComparator>();
        builder.Services.AddTransient<CkModelComparator>();

        // Register core comparison service
        builder.Services.AddTransient<ITenantComparisonService, TenantComparisonService>();

        return builder;
    }
}
