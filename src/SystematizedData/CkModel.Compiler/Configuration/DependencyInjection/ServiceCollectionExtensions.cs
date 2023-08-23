using Meshmakers.Octo.SystematizedData.CkModel.Compiler.ModelRepositories;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Resolvers;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Services;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Validation;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.ModelRepositories;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Resolvers;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Services;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Validation;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCkModelCompiler(
        this IServiceCollection services)
    {
        // Adding resolvers
        services.AddTransient<IDependencyResolver, DependencyResolver>();
        services.AddTransient<IElementResolver, ElementResolver>();
        services.AddTransient<IInheritanceResolver, InheritanceResolver>();
        
        // Adding serializers
        services.AddTransient<ICkSerializer, CkYamlSerializer>();
        services.AddTransient<ICkYamlSerializer, CkYamlSerializer>();
        services.AddTransient<ICkJsonSerializer, CkJsonSerializer>();
        services.AddTransient<ICkSchemaValidator, CkSchemaValidator>();
        
        // Model stuff
        services.AddTransient<ICkModelValidator, CkModelValidator>();
        services.AddSingleton<ICkModelRepositoryManager, CkModelRepositoryManager>();
        
        // Adding services
        services.AddSingleton<ICompilerService, CompilerService>();
        
        
        // Add here sources of Ck model repositories
        services.AddTransient<ICkModelRepository, LocalFileSystemCkModelRepository>();

        return services;
    }
}