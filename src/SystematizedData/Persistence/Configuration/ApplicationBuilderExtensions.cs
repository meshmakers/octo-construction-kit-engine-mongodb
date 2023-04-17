using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.Persistence.Configuration;

public static class ApplicationBuilderExtensions
{
    /// <summary>
    ///     Adds Octo to the pipeline.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns></returns>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IApplicationBuilder UseOctoPersistence(
        this IApplicationBuilder app)
    {
        ConfigureOcto(app).GetAwaiter().GetResult();
        return app;
    }

    private static async Task ConfigureOcto(IApplicationBuilder app)
    {
        var systemContext = app.ApplicationServices.GetRequiredService<ISystemContext>();

        if (!await systemContext.IsSystemDatabaseExistingAsync())
        {
            await systemContext.CreateSystemDatabaseAsync();
        }
    }
}
