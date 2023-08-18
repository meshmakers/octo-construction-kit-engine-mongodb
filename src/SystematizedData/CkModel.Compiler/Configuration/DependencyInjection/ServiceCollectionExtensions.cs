using Meshmakers.Octo.SystematizedData.CkModel.Compiler;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.ModelRepositories;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Resolvers;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Resolvers;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCkModelCompiler(
        this IServiceCollection services)
    {
        // Adding resolvers
        services.AddTransient<IDependencyResolver, DependencyResolver>();
        
        
        
        // Add here sources of Ck model repositories
        services.AddTransient<ICkModelRepository, LocalFileSystemCkModelRepository>();

        return services;
    }
}