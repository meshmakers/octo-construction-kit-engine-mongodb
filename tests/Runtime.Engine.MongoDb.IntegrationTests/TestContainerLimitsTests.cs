using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#8511 — the shared test mongod runs with a fixed footprint: a 1 GB WiredTiger cache and a container memory
///     limit. Without them its cache was sized from the Docker host (125 GiB on the CI node), and several suites on one
///     node starved each other until MongoDB aborted transactions after 60 s. Asked from the server itself, so the
///     test fails if Testcontainers drops the extra command argument or the create-parameter modifier.
/// </summary>
[Collection(SystemCollection.Name)]
public class TestContainerLimitsTests(SystemFixture fixture)
{
    private const long OneGiB = 1024L * 1024 * 1024;

    [Fact]
    public async Task SharedMongod_RunsWithTheCappedWiredTigerCache_AndTheContainerMemoryLimit()
    {
        Assert.SkipWhen(fixture.UsesLocalDatabase, "runs against a local MongoDB, not the shared Testcontainer");
        using var client = CreateAdminProbe();
        var admin = client.GetDatabase("admin");

        var serverStatus = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("serverStatus", 1), cancellationToken: TestContext.Current.CancellationToken);
        var cacheBytes = serverStatus["wiredTiger"]["cache"]["maximum bytes configured"].ToInt64();
        cacheBytes.Should().Be(
            long.Parse(TestContainerLimits.MongoWiredTigerCacheSizeGb) * OneGiB,
            "--wiredTigerCacheSizeGB must reach mongod");

        var hostInfo = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("hostInfo", 1), cancellationToken: TestContext.Current.CancellationToken);
        var memLimitMb = hostInfo["system"]["memLimitMB"].ToInt64();
        memLimitMb.Should().Be(
            TestContainerLimits.MongoMemoryLimitBytes / (1024 * 1024),
            "mongod reads the container's cgroup memory limit");
    }

    private MongoClient CreateAdminProbe()
    {
        var template = (MongoRepositoryClient)fixture.GetService<IAdminRepositoryAccess>().GetRepositoryClient("admin");
        var settings = template.ClientSettings.Clone();
        settings.ApplicationName = "test-container-limits-probe";
        settings.ClusterConfigurator = null;
        settings.ReadConcern = ReadConcern.Default;
        return new MongoClient(settings);
    }
}
