using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Configuration;

public static class ApplicationBuilderExtensions
{
    /// <summary>
    ///     Adds Octo to the pipeline.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns></returns>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IApplicationBuilder UseOctoMongoDbPersistence(
        this IApplicationBuilder app)
    {
        ConfigureOcto(app).GetAwaiter().GetResult();
        return app;
    }

    private static async Task ConfigureOcto(IApplicationBuilder app)
    {
        var systemContext = app.ApplicationServices.GetRequiredService<ISystemContext>();

        if (!await systemContext.IsSystemTenantExistingAsync()) await systemContext.CreateSystemTenantAsync();
    }
}