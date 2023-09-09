using Meshmakers.Octo.SystematizedData.Persistence;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOctoPersistence(
        this IServiceCollection services,
        Action<OctoSystemConfiguration>? setupSystemConfigurationAction = null)
    {
        if (setupSystemConfigurationAction != null)
        {
            services.Configure(setupSystemConfigurationAction);
        }

        services.AddConstructionKit();
        services.AddCkModelSystem();
        services.AddSingleton<ISystemContext, SystemContext>();

        return services;
    }
}
