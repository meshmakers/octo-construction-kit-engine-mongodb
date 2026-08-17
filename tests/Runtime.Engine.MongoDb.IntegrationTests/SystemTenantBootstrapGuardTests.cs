using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Pins the worst instance of AB#4762: bootstrapping the system tenant over an existing database
///     used to drop the entire platform database, and it happened at service startup with no user
///     action at all.
/// </summary>
[Collection(SystemTenantBootstrapCollection.Name)]
public class SystemTenantBootstrapGuardTests(SystemTenantBootstrapFixture fixture)
{
    private const string SurvivorCollection = "ab4762_survivor";

    [Fact]
    public async Task CreateSystemTenant_WhenDatabaseExistsWithoutCkModel_RefusesAndKeepsTheDatabase()
    {
        var systemContext = fixture.GetSystemContext();
        var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;

        var urlBuilder = new MongoUrlBuilder
        {
            Server = new MongoServerAddress(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection
        };
        var systemDatabase = new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(config.SystemDatabaseName);

        Assert.True(await systemContext.IsSystemTenantExistingAsync());

        // Stand-in for the tenant data a real platform database holds, so the assertion below is
        // about data survival rather than merely about the database name still resolving.
        await systemDatabase.GetCollection<BsonDocument>(SurvivorCollection)
            .InsertOneAsync(new BsonDocument("_id", "keep-me"),
                cancellationToken: TestContext.Current.CancellationToken);

        // Reproduce the trigger: the database is present and full, but its System CK model is gone.
        // In production this is a System CK model version bump whose import was skipped, because
        // IsSystemTenantExistingAsync resolves the model at an EXACT version range.
        await systemDatabase.DropCollectionAsync("CkModel", TestContext.Current.CancellationToken);

        Assert.False(await systemContext.IsSystemTenantExistingAsync());

        var exception = await Assert.ThrowsAsync<TenantException>(
            async () => await systemContext.CreateSystemTenantAsync());

        // Explicit, not the generic conflict text: this path has no untrusted caller and it fails
        // host startup, so the operator needs the real cause.
        Assert.Contains("already exists", exception.Message);
        Assert.Contains("System CK model", exception.Message);

        Assert.True(await systemContext.IsDatabaseExistingAsync(config.SystemDatabaseName));

        var survivor = await systemDatabase.GetCollection<BsonDocument>(SurvivorCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", "keep-me"))
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(survivor);
    }
}
