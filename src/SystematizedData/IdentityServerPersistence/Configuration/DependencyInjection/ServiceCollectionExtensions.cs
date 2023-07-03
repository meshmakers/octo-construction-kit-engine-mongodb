// ReSharper disable once CheckNamespace

using System.ComponentModel;
using Meshmakers.Octo.Backend.Persistence.SystemEntities;
using Meshmakers.Octo.Backend.Persistence.SystemStores;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.Configuration.DependencyInjection;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using Microsoft.AspNetCore.Identity;
using OctoObjectIdConverter = Meshmakers.Octo.SystematizedData.Persistence.MongoDb.OctoObjectIdConverter;

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

        services.AddTransient<IOctoClientStore, ClientStore>();
        services.AddScoped<IOctoResourceStore, ResourceStore>();
        services.AddScoped<IOctoPersistentGrantStore, PersistentGrantStore>();
        services.AddScoped<IOctoIdentityProviderStore, IdentityProviderStore>();

        AddIdentity(services, setupAction);

        return services;
    }
    
    private static void AddIdentity(IServiceCollection services, Action<IdentityOptions>? setupAction)
    {
        var builder = services
            .AddIdentity<OctoUser, OctoRole>(setupAction ?? null!)
            .AddRoleStore<OctoRoleStore>()
            .AddUserStore<OctoUserStore>()
            .AddUserManager<UserManager<OctoUser>>()
            .AddRoleManager<RoleManager<OctoRole>>()
            .AddDefaultTokenProviders()
            .AddErrorDescriber<OctoErrorDescriber>();

        // register custom ObjectId TypeConverter
        RegisterTypeConverter<OctoObjectId, OctoObjectIdConverter>();

        if (builder.RoleType != null)
        {
            builder.Services.AddTransient(
                typeof(IRoleStore<>).MakeGenericType(builder.RoleType), typeof(OctoRoleStore));
        }

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