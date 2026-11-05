using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#8281 — the per-database repository clients are released when their cache is disposed with the DI container.
///     Before, nothing ever disposed them: every client is a driver cluster of its own that the driver's process-wide
///     registry keeps alive until its <c>MongoClient</c> is disposed, so every disposed host (each in-process test host,
///     every eviction) left its server monitors, pooled connections and service graph behind. The identity
///     integration suite ended with ~5,300 MongoDB connections and ran out of memory on a loaded CI agent.
///     The accesses under test are built here, not taken from the fixture, so disposing them leaves the shared
///     singletons alone.
/// </summary>
[Collection(SystemCollection.Name)]
public class RepositoryClientDisposalTests(SystemFixture fixture)
{
    [Fact]
    public async Task DisposingTheAdminAccess_DisposesItsCachedAndEvictedClients_AndRefusesNewOnes()
    {
        var databaseName = $"distest{Guid.NewGuid():N}"[..20];
        var access = new AdminRepositoryAccess(fixture.Provider!);
        var evicted = access.GetRepositoryClient(databaseName);
        (await evicted.IsRepositoryExistingAsync(databaseName)).Should().BeFalse("the client is connected");
        access.Invalidate(databaseName);
        var cached = access.GetRepositoryClient(databaseName);
        (await cached.IsRepositoryExistingAsync(databaseName)).Should().BeFalse();
        access.OwnedClientCount.Should().Be(2, "an evicted client stays owned until the access is disposed");
        var evictedApp = ((MongoRepositoryClient)evicted).ClientSettings.ApplicationName;
        var cachedApp = ((MongoRepositoryClient)cached).ClientSettings.ApplicationName;
        using var probe = CreateProbeClient((MongoRepositoryClient)cached);
        (await ServerConnectionCountAsync(probe, evictedApp)).Should().BeGreaterThan(0, "the evicted client is connected");
        (await ServerConnectionCountAsync(probe, cachedApp)).Should().BeGreaterThan(0, "the cached client is connected");

        access.Dispose();

        access.OwnedClientCount.Should().Be(0);
        (await WaitForNoServerConnectionsAsync(probe, evictedApp)).Should()
            .Be(0, "disposing must close the evicted client's sockets on the server, not only drop the object");
        (await WaitForNoServerConnectionsAsync(probe, cachedApp)).Should().Be(0);
        await FluentActions.Awaiting(() => evicted.IsRepositoryExistingAsync(databaseName)).Should()
            .ThrowAsync<ObjectDisposedException>("the evicted client's driver client was disposed");
        await FluentActions.Awaiting(() => cached.IsRepositoryExistingAsync(databaseName)).Should()
            .ThrowAsync<ObjectDisposedException>("the cached client's driver client was disposed");
        FluentActions.Invoking(() => access.GetRepositoryClient(databaseName)).Should()
            .Throw<ObjectDisposedException>("a disposed access must not build a client nobody would release");
        FluentActions.Invoking(access.Dispose).Should().NotThrow("dispose is idempotent");
    }

    [Fact]
    public void DisposingTheUserAccess_DisposesItsCachedAndEvictedClients_AndRefusesNewOnes()
    {
        var databaseName = $"distest{Guid.NewGuid():N}"[..20];
        var access = new UserRepositoryAccess(fixture.Provider!);
        access.GetRepositoryClient(databaseName);
        access.Invalidate(databaseName);
        access.GetRepositoryClient(databaseName);
        access.GetRepositoryClient($"distest{Guid.NewGuid():N}"[..20]);
        access.OwnedClientCount.Should().Be(3);

        access.Dispose();

        access.OwnedClientCount.Should().Be(0);
        FluentActions.Invoking(() => access.GetRepositoryClient(databaseName)).Should()
            .Throw<ObjectDisposedException>();
    }

    [Fact]
    public void TheRegisteredAccesses_AreDisposable_SoTheContainerReleasesTheirClients()
    {
        // The container disposes IDisposable singletons it created when the host shuts down.
        fixture.GetService<IAdminRepositoryAccess>().Should().BeAssignableTo<IDisposable>();
        fixture.GetService<IUserRepositoryAccess>().Should().BeAssignableTo<IDisposable>();
    }

    [Fact]
    public void DisposingAClientThatNeverConnected_DoesNotThrow_AndIsIdempotent()
    {
        var access = new UserRepositoryAccess(fixture.Provider!);
        var client = access.GetRepositoryClient($"distest{Guid.NewGuid():N}"[..20]);

        FluentActions.Invoking(client.Dispose).Should().NotThrow();
        FluentActions.Invoking(client.Dispose).Should().NotThrow();
        FluentActions.Invoking(access.Dispose).Should().NotThrow("an already disposed client is skipped quietly");
    }

    /// <summary>A separate driver client with the same credentials (admin), without the tested client's app name.</summary>
    private static MongoClient CreateProbeClient(MongoRepositoryClient template)
    {
        var settings = template.ClientSettings.Clone();
        settings.ApplicationName = "repository-client-disposal-probe";
        settings.ClusterConfigurator = null;
        settings.ReadConcern = ReadConcern.Default;
        return new MongoClient(settings);
    }

    /// <summary>Open connections (operations and idle sockets) the server attributes to an application name.</summary>
    private static async Task<int> ServerConnectionCountAsync(IMongoClient probe, string applicationName)
    {
        var pipeline = new[]
        {
            new BsonDocument("$currentOp", new BsonDocument
            {
                { "allUsers", true }, { "idleConnections", true }, { "idleSessions", false }, { "localOps", false }
            }),
            new BsonDocument("$match", new BsonDocument("appName", applicationName))
        };
        var cursor = await probe.GetDatabase("admin").AggregateAsync<BsonDocument>(pipeline);
        return (await cursor.ToListAsync()).Count;
    }

    /// <summary>The server notices a closed socket asynchronously; poll briefly.</summary>
    private static async Task<int> WaitForNoServerConnectionsAsync(IMongoClient probe, string applicationName)
    {
        var count = 0;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            count = await ServerConnectionCountAsync(probe, applicationName);
            if (count == 0)
            {
                return 0;
            }

            await Task.Delay(100);
        }

        return count;
    }
}
