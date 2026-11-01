using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#8026 (OD-7): CK method handlers send a success envelope only after their write reached a
///     majority of the replica set (methodConsistency R3/R6). That guarantee is not configuration — it
///     is hard-coded in <see cref="MongoRepositoryClient" />. These tests pin it: write concern
///     <c>majority</c> with a 30 s wtimeout and read concern <c>majority</c>, on the settings object
///     the live driver client of both repository clients carries.
///     <para>
///         No database is involved: the driver creates a <see cref="MongoClient" /> without connecting
///         (server monitoring runs in the background and is disposed at the end of each test).
///     </para>
/// </summary>
public class MongoRepositoryClientConsistencySettingsTests
{
    private const string DatabaseName = "consistency_guard_test";

    // Placeholder credentials: the admin client refuses to build a URL without them. Nothing connects.
    private static readonly OctoSystemConfiguration SystemConfiguration = new()
    {
        DatabaseHost = "localhost:27017",
        AdminUser = "guard-test-admin",
        AdminUserPassword = "guard-test-placeholder",
        DatabaseUser = "guard-test-{0}",
        DatabaseUserPassword = "guard-test-placeholder",
        ReplicaSetName = "rs"
    };

    [Fact]
    public void AdminRepositoryClient_LiveClientSettings_AreMajorityMajority()
    {
        using var provider = BuildServiceProvider();
        var client = new AdminMongoRepositoryClient(NullLogger<AdminMongoRepositoryClient>.Instance,
            Options.Create(SystemConfiguration), provider, DatabaseName);
        try
        {
            AssertMajorityMajority(client.ClientSettings);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public void UserRepositoryClient_LiveClientSettings_AreMajorityMajority()
    {
        using var provider = BuildServiceProvider();
        var client = new UserMongoRepositoryClient(NullLogger<UserMongoRepositoryClient>.Instance,
            Options.Create(SystemConfiguration), provider, DatabaseName);
        try
        {
            AssertMajorityMajority(client.ClientSettings);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public void CreateClientSettings_UrlOptionsCannotWeakenTheConcerns()
    {
        // A connection URL that asks for w:1, a 1 s wtimeout and read concern local must not win:
        // the consistency settings are applied after MongoClientSettings.FromUrl.
        var url = new MongoUrl("mongodb://localhost:27017/?w=1&wtimeoutMS=1000&readConcernLevel=local");
        Assert.Equal(WriteConcern.W1.W, MongoClientSettings.FromUrl(url).WriteConcern.W);

        var settings = MongoRepositoryClient.CreateClientSettings(url);

        AssertMajorityMajority(settings);
    }

    private static void AssertMajorityMajority(MongoClientSettings settings)
    {
        Assert.Equal(WriteConcern.WMajority.W, settings.WriteConcern.W);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.WriteConcern.WTimeout);
        Assert.Equal(ReadConcernLevel.Majority, settings.ReadConcern.Level);
        Assert.True(settings.RetryWrites);
    }

    private static ServiceProvider BuildServiceProvider()
    {
        // The Client getter resolves the command-observability dependencies from the provider.
        var services = new ServiceCollection();
        services.AddLogging(loggingBuilder => loggingBuilder.ClearProviders());
        services.AddOptions<OctoSystemConfiguration>();
        return services.BuildServiceProvider();
    }
}
