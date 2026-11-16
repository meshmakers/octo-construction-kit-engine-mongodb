using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#8511 — the shared test mongod runs with a fixed footprint: a 3 GB WiredTiger cache, a container memory
///     limit and early closing of idle data handles. Without them its cache was sized from the Docker host (125 GiB on
///     the CI node), and several suites on one node starved each other until MongoDB aborted transactions after 60 s;
///     with a 1 GB cache WiredTiger rolled transactions back under cache pressure. Asked from the server itself, so the
///     test fails if Testcontainers drops an extra command argument or the create-parameter modifier.
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

        var parameters = await admin.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                { "getParameter", 1 },
                { "wiredTigerFileHandleCloseIdleTime", 1 },
                { "wiredTigerFileHandleCloseScanInterval", 1 },
                { "wiredTigerFileHandleCloseMinimum", 1 }
            },
            cancellationToken: TestContext.Current.CancellationToken);
        var expected = TestContainerLimits.MongoFileHandleParameters
            .Where(argument => argument != "--setParameter")
            .Select(argument => argument.Split('='))
            .ToDictionary(pair => pair[0], pair => long.Parse(pair[1]));
        expected.Should().HaveCount(3);
        foreach (var (name, value) in expected)
        {
            parameters[name].ToInt64().Should().Be(value, $"--setParameter {name} must reach mongod (idle data handles closed early)");
        }
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
