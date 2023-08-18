using Meshmakers.Octo.SystematizedData.Persistence.Commands;
using Persistence.Commands;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOctoCommands(
        this IServiceCollection services)
    {
        services.AddTransient<IExportRtModelCommand, ExportRtModelCommand>();
        services.AddTransient<IImportCkModelCommand, ImportCkModelCommand>();
        services.AddTransient<IImportRtModelCommand, ImportRtModelCommand>();

        return services;
    }
}