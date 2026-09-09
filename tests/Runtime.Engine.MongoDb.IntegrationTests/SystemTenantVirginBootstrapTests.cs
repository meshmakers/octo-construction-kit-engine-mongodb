using FakeItEasy;

using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.DisplayRules;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Pins the fresh-install bootstrap on a virgin server (AB#4854). In r3.4.93 the engine's own
///     infrastructure collections (lifecycle probe index, setup-retry record) materialized the system
///     database as an empty shell before the bootstrap decided whether to create the system tenant;
///     the bootstrap then refused, the datasource user was never created, and every service was
///     permanently wedged on a MongoDB authentication failure.
/// </summary>
[Collection(VirginSystemCollection.Name)]
public class SystemTenantVirginBootstrapTests(VirginSystemFixture fixture)
{
    private ISystemContext SystemContext => fixture.GetSystemContext();

    private OctoSystemConfiguration Configuration =>
        fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;

    [Fact]
    public async Task StoreReadsAndClaims_OnVirginServer_DoNotMaterializeSystemDatabase()
    {
        await ResetToVirginAsync();

        var lifecycleStore = fixture.GetService<ITenantLifecycleStore>();
        _ = await lifecycleStore.GetAsync("some-tenant", TestContext.Current.CancellationToken);
        _ = await lifecycleStore.ListAsync(TestContext.Current.CancellationToken);
        _ = await lifecycleStore.TryClaimForReconcileAsync("test-owner", TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        var retryStore = fixture.GetService<ITenantSetupRetryStore>();
        _ = await retryStore.TryClaimAsync("test-service", "test-owner", TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30), 10, TestContext.Current.CancellationToken);
        _ = await retryStore.ListAsync(cancellationToken: TestContext.Current.CancellationToken);

        var sweepStore = fixture.GetService<IDisplayRuleSweepStore>();
        _ = await sweepStore.TryClaimAsync("test-owner", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
            10, TestContext.Current.CancellationToken);
        _ = await sweepStore.ListAsync(cancellationToken: TestContext.Current.CancellationToken);

        // None of the reads or update-only claims above may have created the system database — in
        // r3.4.93 the very first lifecycle probe of service startup did, via its createIndexes.
        Assert.False(await SystemContext.IsDatabaseExistingAsync(Configuration.SystemDatabaseName));
    }

    [Fact]
    public async Task Bootstrap_OverInfrastructureShellDatabase_SucceedsAndCreatesDatasourceUser()
    {
        await ResetToVirginAsync();

        // A failed first setup attempt durably records itself — by design — and thereby materializes
        // the system database as an infrastructure-only shell. This is the AB#4854 wedge trigger.
        var retryStore = fixture.GetService<ITenantSetupRetryStore>();
        await retryStore.RecordFailureAsync("IdentityServerPersistence", Configuration.SystemTenantId,
            "transient failure before bootstrap", TestContext.Current.CancellationToken);

        Assert.True(await SystemContext.IsDatabaseExistingAsync(Configuration.SystemDatabaseName));

        // Must answer false — not fail with a MongoDB authentication error on the datasource-user
        // connection, which is what wedged every caller in r3.4.93.
        Assert.False(await SystemContext.IsSystemTenantExistingAsync());
        Assert.True(await SystemContext.IsSystemDatabaseBootstrappableAsync());

        await SystemContext.CreateSystemTenantAsync();

        Assert.True(await IsDatabaseUserExistingAsync(Configuration.SystemDatabaseName));

        // Exercises the datasource-user read end to end: the CK model probe below runs on the user
        // connection that did not exist before the bootstrap.
        Assert.True(await SystemContext.IsSystemTenantExistingAsync());
        Assert.False(await SystemContext.IsSystemDatabaseBootstrappableAsync());

        // The pre-bootstrap retry record survives the bootstrap — it documents the failed attempt.
        var retryRecords = await retryStore.ListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(retryRecords);
    }

    [Fact]
    public async Task EnsureSystemCkModel_OnInfrastructureShell_DoesNotSeedModel()
    {
        await ResetToVirginAsync();

        var retryStore = fixture.GetService<ITenantSetupRetryStore>();
        await retryStore.RecordFailureAsync("IdentityServerPersistence", Configuration.SystemTenantId,
            "transient failure before bootstrap", TestContext.Current.CancellationToken);

        await SystemContext.EnsureSystemCkModelAsync();

        // Seeding the model (as admin) into the shell is what used to make the shell look like a real
        // system database; the shell must stay model-free and bootstrappable.
        var collectionNames = await ListSystemCollectionNamesAsync();
        Assert.DoesNotContain("CkModel", collectionNames);
        Assert.True(await SystemContext.IsSystemDatabaseBootstrappableAsync());
    }

    [Fact]
    public async Task StoreWritePaths_CreateCollectionAndUniqueIndex()
    {
        await ResetToVirginAsync();

        // Fresh store instances: the per-instance _indexEnsured latch of the fixture's singletons may
        // already be set by another test, which would skip the very createIndexes under test here.
        await using var freshProvider = fixture.Services.BuildServiceProvider();

        var lifecycleStore = freshProvider.GetRequiredService<ITenantLifecycleStore>();
        await lifecycleStore.EnsureCreatingAsync("index-tenant", "index-db", Guid.NewGuid(),
            TestContext.Current.CancellationToken);
        Assert.Contains("tenant_lifecycle_tenantId_unique",
            await ListIndexNamesAsync("tenant_lifecycle"));

        var retryStore = freshProvider.GetRequiredService<ITenantSetupRetryStore>();
        await retryStore.RecordFailureAsync("test-service", "index-tenant", "error",
            TestContext.Current.CancellationToken);
        Assert.Contains("tenant_setup_retry_service_tenant_unique",
            await ListIndexNamesAsync("tenant_setup_retry"));

        var sweepStore = freshProvider.GetRequiredService<IDisplayRuleSweepStore>();
        await sweepStore.EnqueueAsync("index-tenant", "index-ck-type", TestContext.Current.CancellationToken);
        Assert.Contains("display_rule_sweep_tenant_type_unique",
            await ListIndexNamesAsync("display_rule_sweep"));
    }

    [Fact]
    public async Task FailedBootstrap_OverInfrastructureShell_DoesNotDropTheShell()
    {
        await ResetToVirginAsync();

        // The shell holds another service's durable bookkeeping — here a setup-retry record. A failed
        // bootstrap attempt must not destroy it: the rollback may only remove what it created itself.
        var retryStore = fixture.GetService<ITenantSetupRetryStore>();
        await retryStore.RecordFailureAsync("IdentityServerPersistence", Configuration.SystemTenantId,
            "transient failure before bootstrap", TestContext.Current.CancellationToken);

        await using var brokenProvider = BuildProviderWithEmptyCatalog();
        var brokenContext = brokenProvider.GetRequiredService<ISystemContext>();
        await Assert.ThrowsAsync<TenantException>(() => brokenContext.CreateSystemTenantAsync());

        Assert.True(await SystemContext.IsDatabaseExistingAsync(Configuration.SystemDatabaseName));
        Assert.Single(await retryStore.ListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await SystemContext.IsSystemDatabaseBootstrappableAsync());
    }

    [Fact]
    public async Task FailedBootstrap_OnVirginServer_RollsBackDatabaseAndUser()
    {
        await ResetToVirginAsync();

        // Pins the AB#1958 rollback for the case it is meant for: the database did not exist before,
        // so a failed bootstrap removes everything it created and the next attempt starts clean.
        await using var brokenProvider = BuildProviderWithEmptyCatalog();
        var brokenContext = brokenProvider.GetRequiredService<ISystemContext>();
        await Assert.ThrowsAsync<TenantException>(() => brokenContext.CreateSystemTenantAsync());

        Assert.False(await SystemContext.IsDatabaseExistingAsync(Configuration.SystemDatabaseName));
        Assert.False(await IsDatabaseUserExistingAsync(Configuration.SystemDatabaseName));
    }

    [Fact]
    public async Task UpdateSystemCkModel_OverInfrastructureShell_DoesNotSeedModel()
    {
        await ResetToVirginAsync();

        var retryStore = fixture.GetService<ITenantSetupRetryStore>();
        await retryStore.RecordFailureAsync("IdentityServerPersistence", Configuration.SystemTenantId,
            "transient failure before bootstrap", TestContext.Current.CancellationToken);

        // Models the check-then-act race deterministically: a caller probed before the shell existed
        // and the seed itself runs after the shell materialized. The seed decision must re-check.
        var probeContext = new UpdateProbeSystemContext(fixture.GetService<ILoggerFactory>(),
            fixture.GetService<IOptions<OctoSystemConfiguration>>(), fixture.Provider!);
        await probeContext.UpdateSystemCkModelDirectAsync();

        Assert.DoesNotContain("CkModel", await ListSystemCollectionNamesAsync());
        Assert.True(await SystemContext.IsSystemDatabaseBootstrappableAsync());
    }

    /// <summary>
    ///     Copies the fixture's registrations and swaps in an empty CK catalog, so a bootstrap fails
    ///     AFTER the database/user creation step — inside the try whose catch performs the rollback.
    /// </summary>
    private ServiceProvider BuildProviderWithEmptyCatalog()
    {
        var services = new ServiceCollection();
        foreach (var descriptor in fixture.Services)
        {
            ((IList<ServiceDescriptor>)services).Add(descriptor);
        }

        services.Replace(ServiceDescriptor.Singleton(A.Fake<ICatalogService>()));
        return services.BuildServiceProvider();
    }

    /// <summary>Exposes the protected seed step so the race window can be pinned directly.</summary>
    private sealed class UpdateProbeSystemContext(ILoggerFactory loggerFactory,
        IOptions<OctoSystemConfiguration> systemConfiguration, IServiceProvider serviceProvider)
        : SystemContext(loggerFactory, systemConfiguration, serviceProvider)
    {
        public Task UpdateSystemCkModelDirectAsync()
        {
            return UpdateSystemCkModelAsync(DatabaseName, TenantId);
        }
    }

    /// <summary>
    ///     Drops the system database and its datasource user, restoring the virgin-server state so the
    ///     facts of this class are order-independent on the shared container.
    /// </summary>
    private async Task ResetToVirginAsync()
    {
        var adminClient = CreateAdminClient();
        await adminClient.DropDatabaseAsync(Configuration.SystemDatabaseName,
            TestContext.Current.CancellationToken);

        var userName = string.Format(Configuration.DatabaseUser, Configuration.SystemDatabaseName);
        var authDatabase = adminClient.GetDatabase(Configuration.AuthenticationDatabaseName);
        try
        {
            await authDatabase.RunCommandAsync<BsonDocument>(
                new BsonDocumentCommand<BsonDocument>(new BsonDocument("dropUser", userName)),
                cancellationToken: TestContext.Current.CancellationToken);
        }
        catch (MongoCommandException)
        {
            // User does not exist — already virgin.
        }

        // Dropping the user voids the authentication of every pooled connection in the cached
        // repository clients (they then fail with "requires authentication" even after the user is
        // re-created — AB#4690), so the cache must be rebuilt for the next test.
        await SystemContext.InvalidateTenantRepositoryClientsAsync(SystemContext.TenantId,
            Configuration.SystemDatabaseName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     Opens an admin-credentialed driver connection, mirroring the helper in
    ///     <c>TenantNamespaceGuardTests</c>.
    /// </summary>
    private MongoClient CreateAdminClient()
    {
        var config = Configuration;
        var urlBuilder = new MongoUrlBuilder
        {
            Server = MongoServerAddress.Parse(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection
        };

        return new MongoClient(urlBuilder.ToMongoUrl());
    }

    private async Task<bool> IsDatabaseUserExistingAsync(string normalizedDatabaseName)
    {
        var config = Configuration;
        var userName = string.Format(config.DatabaseUser, normalizedDatabaseName);
        var authDatabase = CreateAdminClient().GetDatabase(config.AuthenticationDatabaseName);

        var result = await authDatabase.RunCommandAsync<BsonDocument>(
            new BsonDocumentCommand<BsonDocument>(new BsonDocument("usersInfo", userName)));

        return result.GetValue("ok", 0).ToDouble() > 0
               && result.GetValue("users", new BsonArray()).AsBsonArray.Count > 0;
    }

    private async Task<List<string>> ListSystemCollectionNamesAsync()
    {
        var database = CreateAdminClient().GetDatabase(Configuration.SystemDatabaseName);
        var cursor = await database.ListCollectionNamesAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        return await cursor.ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<string>> ListIndexNamesAsync(string collectionName)
    {
        var database = CreateAdminClient().GetDatabase(Configuration.SystemDatabaseName);
        var cursor = await database.GetCollection<BsonDocument>(collectionName).Indexes
            .ListAsync(TestContext.Current.CancellationToken);
        var indexes = await cursor.ToListAsync(TestContext.Current.CancellationToken);
        return indexes.Select(i => i.GetValue("name").AsString).ToList();
    }
}
