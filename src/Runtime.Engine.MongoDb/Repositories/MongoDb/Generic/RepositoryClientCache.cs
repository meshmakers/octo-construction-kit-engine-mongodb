using System.Collections.Concurrent;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Caching.Memory;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     The per-database client cache behind <see cref="AdminRepositoryAccess" /> and <see cref="UserRepositoryAccess" />.
///     Owns every client it builds — evicted ones included — and disposes them all when the owning access is disposed,
///     i.e. when the DI container of the host shuts down.
/// </summary>
/// <remarks>
///     AB#8281: every client is a MongoDB driver cluster of its own (unique application name, own
///     <c>ClusterConfigurator</c>), and the driver's process-wide <c>ClusterRegistry</c> keeps a cluster alive until it is
///     unregistered (see <see cref="MongoRepositoryClient.Dispose" />). Without this the clients of a disposed host were never
///     released: their server monitors kept polling, their pools kept connections open, and the cluster's configurator kept the host's whole
///     service graph (CK cache, OpenIddict, …) reachable. Every in-process test host leaked that way — the identity
///     integration suite ended with ~5,300 MongoDB connections, ~1,500 server monitors, an 8.5 GB mongod and a test
///     process that ran out of memory on a loaded CI agent. <see cref="Invalidate" /> still only evicts (see there);
///     the evicted client is released at shutdown instead of never.
/// </remarks>
internal sealed class RepositoryClientCache<TClient> : IDisposable where TClient : class, IRepositoryClient
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ConcurrentDictionary<TClient, byte> _owned = new(ReferenceEqualityComparer.Instance);
    private readonly Func<string, TClient> _factory;
    private int _disposed;

    public RepositoryClientCache(Func<string, TClient> factory)
    {
        _factory = factory;
    }

    /// <summary>Number of clients this cache built and has not disposed yet (cached and evicted). Test seam.</summary>
    internal int OwnedClientCount => _owned.Count;

    public TClient GetOrCreate(string databaseName)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var client = _cache.GetOrCreate(databaseName.NormalizeString(), _ =>
        {
            // GetOrCreate is not atomic: two concurrent misses may both build a client. The loser is owned as well,
            // so it is disposed at shutdown instead of leaking.
            var newClient = _factory(databaseName);
            _owned.TryAdd(newClient, 0);
            return newClient;
        });

        if (client == null)
        {
            throw TenantException.CannotCreateMongoDbRepositoryClient(databaseName);
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            // Raced with Dispose: the client may have been added after the owned set was drained.
            DisposeClient(client);
            throw new ObjectDisposedException(GetType().FullName);
        }

        return client;
    }

    /// <summary>
    ///     Evict only — never dispose. Handed-out clients are captured by live TenantContext /
    ///     MongoDbRepositoryDataSource instances beyond this cache, and disposing tears down the shared cluster
    ///     underneath them: every in-flight operation then fails with ObjectDisposedException('CoreServerSessionPool')
    ///     — this broke sequential CK batch imports (FixAll), whose PosUpdateTenant event disposed the client between
    ///     two batch steps. Eviction alone is sufficient for AB#4690: future resolves build a fresh,
    ///     freshly-authenticated client. The evicted client stays owned and is disposed with the cache (AB#8281).
    /// </summary>
    public void Invalidate(string databaseName)
    {
        _cache.Remove(databaseName.NormalizeString());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cache.Dispose();
        foreach (var client in _owned.Keys)
        {
            DisposeClient(client);
        }
    }

    private void DisposeClient(TClient client)
    {
        if (!_owned.TryRemove(client, out _))
        {
            return;
        }

        try
        {
            client.Dispose();
        }
        catch (Exception)
        {
            // Shutdown is best effort: one client that fails to close must not keep the others open.
        }
    }
}
