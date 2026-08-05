using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

internal class UserRepositoryAccess(IServiceProvider serviceProvider) : IUserRepositoryAccess
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public IRepositoryClient GetRepositoryClient(string databaseName)
    {
        var client = _cache.GetOrCreate(databaseName.NormalizeString(), _ =>
        {
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var systemConfiguration = serviceProvider.GetRequiredService<IOptions<OctoSystemConfiguration>>();

            var newClient = new UserMongoRepositoryClient(loggerFactory.CreateLogger<UserMongoRepositoryClient>(),
                systemConfiguration, serviceProvider, databaseName);
            return newClient;
        });

        if (client == null)
        {
            throw TenantException.CannotCreateMongoDbRepositoryClient(databaseName);
        }

        return client;
    }

    public void Invalidate(string databaseName)
    {
        // Evict only — never dispose. Handed-out clients are captured by live TenantContext /
        // MongoDbRepositoryDataSource instances beyond this cache, and disposing tears down the shared
        // cluster underneath them: every in-flight operation then fails with
        // ObjectDisposedException('CoreServerSessionPool') — this broke sequential CK batch imports
        // (FixAll), whose PosUpdateTenant event disposed the client between two batch steps.
        // Eviction alone is sufficient for AB#4690: future resolves build a fresh, freshly-authenticated
        // client; the evicted one is collected once its holders let go, and its stale connections are
        // closed by the server / pool idle handling.
        _cache.Remove(databaseName.NormalizeString());
    }
}