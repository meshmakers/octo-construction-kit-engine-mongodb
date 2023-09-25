// ReSharper disable once CheckNamespace

using Duende.IdentityServer.Models;
using Meshmakers.Octo.Backend.Persistence.SystemStores;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.Configuration.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using Persistence.IdentityCkModel.ConstructionKit.Generated.System.Identity.v1;

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

        services.AddTransient<IOctoClientStore, ClientStore>();
        services.AddScoped<IOctoResourceStore, ResourceStore>();
        services.AddScoped<IOctoPersistentGrantStore, PersistentGrantStore>();
        services.AddScoped<IOctoIdentityProviderStore, IdentityProviderStore>();
        services.AddAutoMapper(cfg =>
        {
            cfg.CreateMap<RtClient, Client>();
            cfg.CreateMap<RtPersistedGrant, PersistedGrant>()
                .ForMember(dest => dest.Type, opt => opt.MapFrom(src => src.GrantType))
                .ForMember(dest => dest.Key, opt => opt.MapFrom(src => src.GrantKey))
                .ForMember(dest => dest.CreationTime, opt => opt.MapFrom(src => src.CreationDateTime))
                .ForMember(dest => dest.Expiration, opt => opt.MapFrom(src => src.ExpirationDateTime));
            cfg.CreateMap<RtIdentityResource, IdentityResource>()
                .ForMember(dest => dest.Emphasize, opt => opt.MapFrom(src => src.IsEmphasized))
                .ForMember(dest => dest.Required, opt => opt.MapFrom(src => src.IsRequired));
            cfg.CreateMap<RtApiResource, ApiResource>();
            cfg.CreateMap<RtApiScope, ApiScope>()
                .ForMember(dest => dest.Emphasize, opt => opt.MapFrom(src => src.IsEmphasized))
                .ForMember(dest => dest.Required, opt => opt.MapFrom(src => src.IsRequired));

        });

        AddIdentity(services, setupAction);

        return services;
    }
    
    private static void AddIdentity(IServiceCollection services, Action<IdentityOptions>? setupAction)
    {
        var builder = services
            .AddIdentity<RtUser, RtRole>(setupAction ?? null!)
            .AddRoleStore<OctoRoleStore>()
            .AddUserStore<OctoUserStore>()
            .AddUserManager<UserManager<RtUser>>()
            .AddRoleManager<RoleManager<RtRole>>()
            .AddDefaultTokenProviders()
            .AddErrorDescriber<OctoErrorDescriber>();

        if (builder.RoleType != null)
        {
            builder.Services.AddTransient(
                typeof(IRoleStore<>).MakeGenericType(builder.RoleType), typeof(OctoRoleStore));
        }

        builder.Services.AddTransient(
            typeof(IUserStore<>).MakeGenericType(builder.UserType), typeof(OctoUserStore));
    }
}