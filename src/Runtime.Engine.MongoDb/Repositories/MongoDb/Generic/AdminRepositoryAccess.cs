using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

internal class AdminRepositoryAccess(IServiceProvider serviceProvider) : IAdminRepositoryAccess
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public IAdminRepositoryClient GetRepositoryClient(string databaseName)
    {
        var client = _cache.GetOrCreate(databaseName.NormalizeString(), _ =>
        {
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var systemConfiguration = serviceProvider.GetRequiredService<IOptions<OctoSystemConfiguration>>();
        
            var newClient = new AdminMongoRepositoryClient(loggerFactory.CreateLogger<AdminMongoRepositoryClient>(), systemConfiguration, serviceProvider, databaseName);
            return newClient;
        });

        if (client == null)
        {
            throw TenantException.CannotCreateMongoDbRepositoryClient(databaseName);
        }

        return client;
    }
}