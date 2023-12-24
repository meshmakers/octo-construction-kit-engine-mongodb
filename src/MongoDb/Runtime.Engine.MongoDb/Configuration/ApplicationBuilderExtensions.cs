using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Configuration;

public static class ApplicationBuilderExtensions
{
    /// <summary>
    ///     Adds Octo to the pipeline.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns></returns>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IHost UseOctoMongoDbPersistence(
        this IHost app)
    {
        ConfigureOcto(app).GetAwaiter().GetResult();
        return app;
    }

    private static async Task ConfigureOcto(IHost app)
    {
        var systemContext = app.Services.GetRequiredService<ISystemContext>();

        if (!await systemContext.IsSystemTenantExistingAsync())
        {
            await systemContext.CreateSystemTenantAsync();
        }
    }
}