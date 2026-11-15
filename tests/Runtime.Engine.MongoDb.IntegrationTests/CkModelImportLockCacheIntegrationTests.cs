using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.ConstructionKit.Engine.Resolvers.Repository;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#8947: the dependency resolve no longer asks the lock collection (<c>SysLock</c>) on every call, while the
///     AB#8677 guarantee stays: a reader in another process that resolves a model during its in-place upgrade waits for
///     the import and then resolves the new version. "Another process" is a <see cref="CkModelImportLockCache" /> of its
///     own (the process-wide <see cref="CkModelImportLockCache.Shared" /> is what the services in this process use).
/// </summary>
[Collection(CkModelImportMigrationCollection.Name)]
public class CkModelImportLockCacheIntegrationTests(CkModelImportMigrationFixture fixture)
{
    private const string LockedModel = "LockedModel";

    /// <summary>
    ///     The held lock is seen at once by every data source of this process (the lock is acquired through one instance,
    ///     read through another) and at most one TTL late by another process; the release likewise.
    /// </summary>
    [Fact]
    public async Task HeldImportLock_IsSeenAtOnceInThisProcess_AndAtMostOneTtlLateInAnother()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"lockcache{Guid.NewGuid():N}"[..20];
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await CreateChildAsync(tenantId);
            var importer = NewDataSource(tenantId);
            var reader = NewDataSource(tenantId);
            var clock = new ManualTimeProvider();
            var otherProcess = new CkModelImportLockCache(CkModelImportLockCache.DefaultTimeToLive, clock);
            var ttl = otherProcess.TimeToLive;

            Assert.Empty(await CkModelImportLockCache.Shared.GetModelsBeingImportedAsync(reader, ct));
            Assert.Empty(await otherProcess.GetModelsBeingImportedAsync(reader, ct));

            await using (await importer.AcquireModelImportLockAsync(LockedModel, ct))
            {
                Assert.Equal([LockedModel], await CkModelImportLockCache.Shared.GetModelsBeingImportedAsync(reader, ct));

                // Worst case of the other process: its snapshot from before the acquire is used for up to one TTL ...
                clock.Advance(ttl - TimeSpan.FromMilliseconds(1));
                Assert.Empty(await otherProcess.GetModelsBeingImportedAsync(reader, ct));
                // ... and not a moment longer.
                clock.Advance(TimeSpan.FromMilliseconds(1));
                Assert.Equal([LockedModel], await otherProcess.GetModelsBeingImportedAsync(reader, ct));
            }

            Assert.Empty(await CkModelImportLockCache.Shared.GetModelsBeingImportedAsync(reader, ct));
            Assert.Equal([LockedModel], await otherProcess.GetModelsBeingImportedAsync(reader, ct));
            clock.Advance(ttl);
            Assert.Empty(await otherProcess.GetModelsBeingImportedAsync(reader, ct));
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    /// <summary>
    ///     AC 3: a reader in another process resolves a model again and again while the model is upgraded in place. Its
    ///     cached "no import running" may predate the import (the importer does not wait for it), but a miss is checked
    ///     against the lock collection directly, so the reader always finds a version (the old one, or the new one
    ///     after waiting for the import) and never "does not exist".
    /// </summary>
    [Fact]
    public async Task ReaderInAnotherProcess_DuringAnInPlaceUpgrade_AlwaysResolvesAVersion()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"lockupg{Guid.NewGuid():N}"[..20];
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await CreateChildAsync(tenantId);
            var tenant = await GetChildAsync(tenantId);
            var system = await GetInstalledSystemIdAsync(tenant);
            await tenant.ImportCkModelAsync(OnSystem("LockUpg-1.0.0", system));

            var otherProcess = new CkModelImportLockCache(CkModelImportLockCache.DefaultTimeToLive, TimeProvider.System);
            var reader = new DatabaseCkModelRepository(NullLogger<DatabaseCkModelRepository>.Instance,
                fixture.GetService<IRepositoryModelResolver>(), fixture.GetService<ICkModelImportAuditTrail>(),
                otherProcess);
            var source = new TenantDatabaseSourceIdentifier(null, NewDataSource(tenantId), tenantId)
            {
                CancellationToken = ct
            };
            var range = new CkModelIdVersionRange("LockUpg", "[1.0,2.0)");
            // The reader's snapshot is taken now, before the import holds the lock.
            Assert.True((await reader.IsExistingAsync(range, source)).Exists);

            var results = new System.Collections.Concurrent.ConcurrentQueue<CkModelId?>();
            using var stop = new CancellationTokenSource();
            var readerLoop = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    // A fresh deadline per resolve, as every real resolve has.
                    var result = await reader.IsExistingAsync(range, source with { ImportWaitDeadline = new() });
                    results.Enqueue(result.Exists ? result.ModelId : null);
                    await Task.Delay(20, CancellationToken.None);
                }
            }, CancellationToken.None);

            try
            {
                await tenant.ImportCkModelAsync(OnSystem("LockUpg-1.1.0", system));
            }
            finally
            {
                // One more round after the import, so the last answer is the new version.
                await Task.Delay(CkModelImportLockCache.DefaultTimeToLive + TimeSpan.FromMilliseconds(200),
                    CancellationToken.None);
                await stop.CancelAsync();
                await readerLoop;
            }

            var answers = results.ToList();
            Assert.True(answers.Count > 10, $"reader ran only {answers.Count} times");
            Assert.DoesNotContain(null, answers);
            Assert.Equal(new CkModelId("LockUpg-1.1.0"), answers[^1]);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    /// <summary>
    ///     Rolling deployment: the importer runs an older package that neither signals this cache nor waits, and the
    ///     reader's cached answer ("nothing running") never expires (frozen clock). While the import holds the lock and
    ///     the model has no Available version, the reader does not answer "does not exist": it finds the import through
    ///     the direct check on its miss, waits, and resolves the version the import leaves behind.
    /// </summary>
    [Fact]
    public async Task RollingDeployment_ImporterWithoutTheSignal_ReaderWithAStaleAnswer_WaitsInsteadOfMissing()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"lockroll{Guid.NewGuid():N}"[..20];
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await CreateChildAsync(tenantId);
            var tenant = await GetChildAsync(tenantId);
            var system = await GetInstalledSystemIdAsync(tenant);
            await tenant.ImportCkModelAsync(OnSystem("LockRoll-1.0.0", system));
            var models = AdminDatabase(tenantId.ToLowerInvariant()).GetCollection<BsonDocument>("CkModel");
            var row = new BsonDocument("_id", "LockRoll-1.0.0");

            var reader = new DatabaseCkModelRepository(NullLogger<DatabaseCkModelRepository>.Instance,
                fixture.GetService<IRepositoryModelResolver>(), fixture.GetService<ICkModelImportAuditTrail>(),
                new CkModelImportLockCache(CkModelImportLockCache.DefaultTimeToLive, new ManualTimeProvider()));
            var source = new TenantDatabaseSourceIdentifier(null, NewDataSource(tenantId), tenantId)
            {
                CancellationToken = ct
            };
            var range = new CkModelIdVersionRange("LockRoll", "[1.0,2.0)");
            // The stale answer: taken before the import, and it never expires.
            Assert.True((await reader.IsExistingAsync(range, source)).Exists);

            // The "old" importer: its lock is invisible to the reader's cache. Then it removes the Available version.
            var importer = NewDataSource(tenantId);
            var tenantDatabase = AdminDatabase(tenantId.ToLowerInvariant());
            IDistributedLockHandle? importLock = await importer.AcquireModelImportLockAsync("LockRoll", ct);
            try
            {
                await models.UpdateOneAsync(row, new BsonDocument("$set", new BsonDocument("modelState", 0)),
                    cancellationToken: ct);
                await tenantDatabase.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 2), cancellationToken: ct);
                var lockReadsBefore = await CountLockReadsAsync([tenantDatabase], ct);

                var resolve = reader.IsExistingAsync(range, source with { ImportWaitDeadline = new() });
                // Checkpoint: the reader has read the lock collection (direct check on its miss, then the wait loop).
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (await CountLockReadsAsync([tenantDatabase], ct) == lockReadsBefore && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50, ct);
                }

                Assert.True(await CountLockReadsAsync([tenantDatabase], ct) > lockReadsBefore,
                    "the reader never checked the lock collection");
                Assert.False(resolve.IsCompleted, "the reader answered while the import still held the lock");

                // The import finishes: the version is Available again and the lock is released.
                await models.UpdateOneAsync(row, new BsonDocument("$set", new BsonDocument("modelState", 1)),
                    cancellationToken: ct);
                await importLock.DisposeAsync();
                importLock = null;

                var result = await resolve;
                Assert.True(result.Exists);
                Assert.Equal(new CkModelId("LockRoll-1.0.0"), result.ModelId);
            }
            finally
            {
                if (importLock != null)
                {
                    await importLock.DisposeAsync();
                }

                await tenantDatabase.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 0),
                    cancellationToken: CancellationToken.None);
            }
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    /// <summary>
    ///     The hot caller on test-2: every tenant lookup (<see cref="ISystemContext.TryFindTenantContextAsync" />) runs
    ///     <c>IsSystemTenantExistingAsync</c>, which resolves the System model outside a transaction. Before AB#8947
    ///     every lookup cost one <c>find</c> on <c>SysLock</c>; now at most one per TTL. The "before" rounds drop the
    ///     cached answer before each lookup, which is exactly what every lookup did before the cache existed. The
    ///     <c>SysLock</c> reads are counted with the MongoDB profiler of the system and the tenant database, all
    ///     commands of a lookup with <see cref="MongoRequestScope" />.
    /// </summary>
    [Fact]
    public async Task TenantLookup_ReadsTheLockCollectionOncePerTtl_NotOncePerLookup()
    {
        const int lookups = 10;
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"lockreq{Guid.NewGuid():N}"[..20];
        var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        var systemDatabaseName = config.SystemDatabaseName.Trim().ToLowerInvariant();
        var systemDataSource = NewDataSource(systemDatabaseName, config.SystemTenantId);
        var profiled = new[] { AdminDatabase(systemDatabaseName), AdminDatabase(tenantId.ToLowerInvariant()) };
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await CreateChildAsync(tenantId);
            Assert.NotNull(await systemContext.TryFindTenantContextAsync(tenantId));

            foreach (var database in profiled)
            {
                await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 2), cancellationToken: ct);
            }

            // Cumulative counts: the profiler stamps the server's clock, which a test-side time window cannot match.
            var lockReadsAtStart = await CountLockReadsAsync(profiled, ct);
            var beforeCommands = 0;
            for (var i = 0; i < lookups; i++)
            {
                CkModelImportLockCache.Shared.NotifyImportLockChanged(systemDataSource);
                using (MongoRequestScope.Begin(out var stats))
                {
                    Assert.NotNull(await systemContext.TryFindTenantContextAsync(tenantId));
                    beforeCommands += stats.CommandCount;
                }
            }

            var lockReadsAfterBeforeRounds = await CountLockReadsAsync(profiled, ct);
            var afterRounds = Stopwatch.StartNew();
            var afterCommands = 0;
            for (var i = 0; i < lookups; i++)
            {
                using (MongoRequestScope.Begin(out var stats))
                {
                    Assert.NotNull(await systemContext.TryFindTenantContextAsync(tenantId));
                    afterCommands += stats.CommandCount;
                }
            }

            afterRounds.Stop();
            var lockReadsBefore = lockReadsAfterBeforeRounds - lockReadsAtStart;
            var lockReadsAfter = await CountLockReadsAsync(profiled, ct) - lockReadsAfterBeforeRounds;

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{lookups} tenant lookups: SysLock reads before {lockReadsBefore}, after {lockReadsAfter}; " +
                $"MongoDB commands before {beforeCommands}, after {afterCommands}");
            Assert.Equal(lookups, lockReadsBefore);
            // One read per TTL window the "after" rounds touched: a slow agent may cross a few windows.
            var ttlWindows = (int)(afterRounds.Elapsed / CkModelImportLockCache.DefaultTimeToLive) + 1;
            Assert.True(lockReadsAfter <= ttlWindows,
                $"{lockReadsAfter} SysLock reads in {lookups} lookups over {afterRounds.Elapsed} ({ttlWindows} TTL windows)");
            Assert.True(beforeCommands - afterCommands >= lookups - ttlWindows,
                $"MongoDB commands for {lookups} lookups: before {beforeCommands}, after {afterCommands}, " +
                $"{ttlWindows} TTL windows");
        }
        finally
        {
            foreach (var database in profiled)
            {
                await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 0),
                    cancellationToken: CancellationToken.None);
            }

            await profiled[0].DropCollectionAsync("system.profile", CancellationToken.None);
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    private static async Task<long> CountLockReadsAsync(IEnumerable<IMongoDatabase> databases, CancellationToken ct)
    {
        long count = 0;
        foreach (var database in databases)
        {
            var filter = new BsonDocument
            {
                { "ns", $"{database.DatabaseNamespace.DatabaseName}.SysLock" },
                { "op", "query" }
            };
            count += await database.GetCollection<BsonDocument>("system.profile")
                .CountDocumentsAsync(filter, cancellationToken: ct);
        }

        return count;
    }

    private static CkCompiledModelRoot OnSystem(string modelId, CkModelId system)
    {
        var id = new CkModelId(modelId);
        return new CkCompiledModelRoot
        {
            ModelId = id,
            Description = "AB#8947 model on System",
            Dependencies = [system],
            Types =
            [
                new CkCompiledTypeDto
                {
                    TypeId = new CkTypeId($"{id.Name}Thing-1"),
                    DerivedFromCkTypeId = new CkId<CkTypeId>(system, new CkTypeId("Entity-1"))
                }
            ]
        };
    }

    private MongoDbRepositoryDataSource NewDataSource(string tenantId) =>
        NewDataSource(tenantId.ToLowerInvariant(), tenantId);

    private MongoDbRepositoryDataSource NewDataSource(string databaseName, string tenantId)
    {
        return new MongoDbRepositoryDataSource(NullLogger<MongoDbRepositoryDataSource>.Instance,
            fixture.GetService<IAdminRepositoryAccess>().GetRepositoryClient(databaseName), databaseName, tenantId);
    }

    private IMongoDatabase AdminDatabase(string databaseName)
    {
        var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        var urlBuilder = new MongoUrlBuilder
        {
            Server = MongoServerAddress.Parse(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection
        };
        return new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(databaseName);
    }

    private async Task<CkModelId> GetInstalledSystemIdAsync(ITenantContext tenant)
    {
        var catalogService = fixture.GetService<ICatalogService>();
        foreach (var version in (await catalogService.ListVersionsAsync("System")).OrderByDescending(v => v.ModelId))
        {
            if (await tenant.IsCkModelExistingAsync(version.ModelId))
            {
                return version.ModelId;
            }
        }

        throw new InvalidOperationException("No installed System model found in the catalogs.");
    }

    private async Task CreateChildAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task<ITenantContext> GetChildAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        var child = await systemContext.GetChildTenantContextAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return child;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
