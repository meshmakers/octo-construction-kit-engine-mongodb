using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     Process-wide (singleton) cache of the user repository clients, one per database. Disposed with the DI container,
///     which disposes every client it built (AB#8281, see <see cref="RepositoryClientCache{TClient}" />).
/// </summary>
internal sealed class UserRepositoryAccess : IUserRepositoryAccess, IDisposable
{
    private readonly RepositoryClientCache<UserMongoRepositoryClient> _clients;

    public UserRepositoryAccess(IServiceProvider serviceProvider)
    {
        _clients = new RepositoryClientCache<UserMongoRepositoryClient>(databaseName => new UserMongoRepositoryClient(
            serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<UserMongoRepositoryClient>(),
            serviceProvider.GetRequiredService<IOptions<OctoSystemConfiguration>>(), serviceProvider, databaseName));
    }

    /// <summary>Clients built and not yet disposed (cached and evicted). Test seam.</summary>
    internal int OwnedClientCount => _clients.OwnedClientCount;

    public IRepositoryClient GetRepositoryClient(string databaseName) => _clients.GetOrCreate(databaseName);

    /// <inheritdoc cref="RepositoryClientCache{TClient}.Invalidate" />
    public void Invalidate(string databaseName) => _clients.Invalidate(databaseName);

    public void Dispose() => _clients.Dispose();
}
