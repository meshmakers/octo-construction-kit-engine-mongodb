using System;
using System.ComponentModel;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.Configuration.DependencyInjection;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;
using Meshmakers.Octo.SystematizedData.Persistence.SystemStores;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using IdentityServiceCollectionExtensions = Microsoft.AspNetCore.Identity.IdentityBuilderExtensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOctoPersistence(
        this IServiceCollection services,
        Action<OctoSystemConfiguration> setupSystemConfigurationAction = null,
        Action<IdentityOptions> setupAction = null)
    {
        if (setupSystemConfigurationAction != null)
        {
            services.Configure(setupSystemConfigurationAction);
        }


        services.AddSingleton<ISystemContext, SystemContext>();
        services.AddTransient<IOctoClientStore, ClientStore>();
        services.AddScoped<IOctoResourceStore, ResourceStore>();
        services.AddScoped<IOctoPersistentGrantStore, PersistentGrantStore>();
        services.AddScoped<IOctoIdentityProviderStore, IdentityProviderStore>();

        AddIdentity(services, setupAction);

        return services;
    }

    private static void AddIdentity(IServiceCollection services, Action<IdentityOptions> setupAction)
    {
        var builder = services
            .AddIdentity<OctoUser, OctoRole>(setupAction)
            .AddRoleStore<OctoRoleStore>()
            .AddUserStore<OctoUserStore>()
            .AddUserManager<UserManager<OctoUser>>()
            .AddRoleManager<RoleManager<OctoRole>>()
            .AddDefaultTokenProviders()
            .AddErrorDescriber<OctoErrorDescriber>();

        // register custom ObjectId TypeConverter
        RegisterTypeConverter<ObjectId, ObjectIdConverter>();

        builder.Services.AddTransient(
            typeof(IRoleStore<>).MakeGenericType(builder.RoleType), typeof(OctoRoleStore));

        builder.Services.AddTransient(
            typeof(IUserStore<>).MakeGenericType(builder.UserType), typeof(OctoUserStore));
    }

    private static void RegisterTypeConverter<T, TC>() where TC : TypeConverter
    {
        var attr = new Attribute[1];
        var vConv = new TypeConverterAttribute(typeof(TC));
        attr[0] = vConv;
        TypeDescriptor.AddAttributes(typeof(T), attr);
    }
}
