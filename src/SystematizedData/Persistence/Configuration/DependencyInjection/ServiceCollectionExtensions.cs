using System;
using Meshmakers.Octo.SystematizedData.Persistence;
using Microsoft.AspNetCore.Identity;
using IdentityServiceCollectionExtensions = Microsoft.AspNetCore.Identity.IdentityBuilderExtensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOctoPersistence(
        this IServiceCollection services,
        Action<OctoSystemConfiguration>? setupSystemConfigurationAction = null,
        Action<IdentityOptions>? setupAction = null)
    {
        if (setupSystemConfigurationAction != null)
        {
            services.Configure(setupSystemConfigurationAction);
        }

        services.AddSingleton<ISystemContext, SystemContext>();

        return services;
    }
}
