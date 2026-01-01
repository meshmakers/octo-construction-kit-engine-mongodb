using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Engine.ModelRepositories;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Runtime.Engine.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Blueprints;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Defaults;

using Microsoft.Extensions.DependencyInjection.Extensions;

using IMongoTenantBackupService = Meshmakers.Octo.Runtime.Contracts.MongoDb.Services.ITenantBackupService;
using IBlueprintBackupService = Meshmakers.Octo.Runtime.Contracts.Blueprints.ITenantBackupService;

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
        builder.Services.AddCkModelSystemV2();

        // Add services of Persistence module
        builder.Services.AddTransient<IDatabaseCkModelRepository, DatabaseCkModelRepository>();
        builder.Services.AddTransient<IModelRepository>(sp => sp.GetRequiredService<IDatabaseCkModelRepository>());
        builder.Services.AddSingleton<ISystemContext, SystemContext>();
        builder.Services.AddSingleton<IModelLoaderService, ModelLoaderService>();
        builder.Services.AddSingleton<IMetricsContext, MetricsContext>();
        builder.Services.TryAddSingleton<Meshmakers.Octo.Runtime.Contracts.MongoDb.Services.ITenantNotifications, DefaultTenantNotifications>();
        builder.Services.AddTransient<IRepositoryOpsService, RepositoryOpsService>();
        builder.Services.AddTransient<IMongoTenantBackupService, TenantBackupService>();

        builder.Services.AddSingleton<IUserRepositoryAccess, UserRepositoryAccess>();
        builder.Services.AddSingleton<IAdminRepositoryAccess, AdminRepositoryAccess>();

        // Add pre-document modification services
        builder.Services.AddTransient<IPreDocumentModification<RtEntity>, AutoIncrementModifier>();


        MongoRepositoryClient.RegisterSerializers();

        return builder;
    }

    /// <summary>
    /// Adds MongoDB-specific blueprint support to the runtime engine.
    /// This enables creating tenants with blueprints and managing blueprint history.
    /// </summary>
    /// <param name="builder">The runtime engine builder</param>
    /// <returns>The builder for chaining</returns>
    /// <remarks>
    /// This method registers the following MongoDB-specific services:
    /// - ITenantBlueprintHistory: MongoDB implementation for tracking blueprint application history
    /// - ITenantBackupService (from Blueprints): Adapter for MongoDB backup functionality
    ///
    /// Note: You must also call AddBlueprints() from the Engine assembly to register
    /// IBlueprintService and related services.
    ///
    /// Usage:
    /// <code>
    /// services.AddRuntimeEngine()
    ///     .AddBlueprints()                // From Engine assembly
    ///     .AddMongoDbRuntimeRepository()
    ///     .AddMongoBlueprintSupport();    // From MongoDB assembly
    /// </code>
    /// </remarks>
    public static IRuntimeEngineBuilder AddMongoBlueprintSupport(this IRuntimeEngineBuilder builder)
    {
        // Register MongoDB-specific blueprint implementations
        builder.Services.AddTransient<ITenantBlueprintHistory, MongoTenantBlueprintHistory>();
        builder.Services.AddTransient<IBlueprintBackupService, MongoBlueprintBackupService>();

        return builder;
    }
}
