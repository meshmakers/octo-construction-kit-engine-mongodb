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
        var key = databaseName.NormalizeString();
        if (!_cache.TryGetValue(key, out IRepositoryClient? cached))
        {
            return;
        }

        _cache.Remove(key);

        // Disposing tears down the cluster, so the stale (now unauthenticated) connections go away
        // immediately instead of lingering until the server or the pool happens to close them.
        try
        {
            cached?.Dispose();
        }
        catch (Exception)
        {
            // A client that fails to shut down cleanly must not break the tenant lifecycle event that
            // triggered the invalidation — it is already out of the cache and will not be handed out again.
        }
    }
}