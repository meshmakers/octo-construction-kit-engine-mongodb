using System.Diagnostics;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Covers the invariants introduced by the recent SysLock changes:
/// owner-token fencing, TTL index, holder-info diagnostics, stale-lock reclaim,
/// concurrent acquire mutual-exclusion, lock-lost signalling, and acquire cancellation.
/// </summary>
[Collection("Sequential")]
public class RepositoryDistributedLockServiceTests : IClassFixture<SystemFixture>
{
    private readonly SystemFixture _fixture;

    public RepositoryDistributedLockServiceTests(SystemFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _fixture.OutputHelper = output;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RepositoryDistributedLockService CreateLockService(string lockId)
    {
        var (client, repo) = GetClientAndRepo();
        var logger = _fixture.GetService<ILoggerFactory>().CreateLogger("LockTest");
        return new RepositoryDistributedLockService(client, repo, logger, lockId);
    }

    private IMongoCollection<SysLock> GetSysLockCollection()
    {
        var (_, repo) = GetClientAndRepo();
        return repo.GetCollection(new LockMongoDataSourceMapper()).GetMongoCollection();
    }

    private (IRepositoryClient client, IRepositoryInternal repo) GetClientAndRepo()
    {
        var userAccess = _fixture.GetService<IUserRepositoryAccess>();
        var config = _fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        var dbName = config.SystemDatabaseName.ToLower();
        var client = userAccess.GetRepositoryClient(dbName);
        var repo = (IRepositoryInternal)client.GetRepository(dbName);
        return (client, repo);
    }

    private static string NewLockId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    [Fact]
    public async Task Acquire_PersistsOwnerTokenAndHolderInfo()
    {
        var lockId = NewLockId("test_owner");
        await using var lockService = CreateLockService(lockId);

        await lockService.AcquireLockAsync(Ct);

        var doc = await GetSysLockCollection().Find(s => s.Id == lockId).FirstOrDefaultAsync(Ct);
        Assert.NotNull(doc);
        Assert.NotNull(doc.OwnerToken);
        Assert.NotEqual(Guid.Empty, doc.OwnerToken!.Value);
        Assert.False(string.IsNullOrEmpty(doc.HolderInfo));
        Assert.Contains("/", doc.HolderInfo); // "{machine}/{pid}"
        Assert.NotNull(doc.ExpiryDateTime);
        Assert.True(doc.ExpiryDateTime > DateTime.UtcNow);
    }

    [Fact]
    public async Task Acquire_CreatesTtlIndexOnExpiryDateTime()
    {
        // Trigger at least one acquire so EnsureTtlIndex has been called.
        var lockId = NewLockId("test_ttl");
        await using (var lockService = CreateLockService(lockId))
        {
            await lockService.AcquireLockAsync(Ct);
        }

        var indexes = await GetSysLockCollection().Indexes.List(Ct).ToListAsync(Ct);
        var ttlIndex = indexes.FirstOrDefault(i =>
            i.Contains("name") && i["name"].AsString == "sysLock_ttl_expiryDateTime");

        Assert.NotNull(ttlIndex);
        Assert.True(ttlIndex.Contains("expireAfterSeconds"),
            "TTL index must declare expireAfterSeconds for MongoDB to reap expired lock documents");
        Assert.Equal(0, ttlIndex["expireAfterSeconds"].ToInt32());
        Assert.Equal(1, ttlIndex["key"]["expiryDateTime"].ToInt32());
    }

    [Fact]
    public async Task Acquire_ReclaimsStaleLockQuickly()
    {
        var lockId = NewLockId("test_stale");
        var collection = GetSysLockCollection();

        // Simulate a lock left behind by a crashed process: expired and with a foreign owner.
        var staleOwnerToken = Guid.NewGuid();
        await collection.InsertOneAsync(new SysLock
        {
            Id = lockId,
            CreationDateTime = DateTime.UtcNow.AddMinutes(-30),
            ExpiryDateTime = DateTime.UtcNow.AddMinutes(-5),
            LastHeartbeat = DateTime.UtcNow.AddMinutes(-10),
            OwnerToken = staleOwnerToken,
            HolderInfo = "test/zombie"
        }, cancellationToken: Ct);

        await using var lockService = CreateLockService(lockId);

        var sw = Stopwatch.StartNew();
        await lockService.AcquireLockAsync(Ct);
        sw.Stop();

        // First poll iteration sees expired lock and immediately reclaims — well under one TTL.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Stale-lock reclaim should be near-instant, took {sw.ElapsedMilliseconds}ms");

        var doc = await collection.Find(s => s.Id == lockId).FirstOrDefaultAsync(Ct);
        Assert.NotNull(doc);
        Assert.NotEqual(staleOwnerToken, doc.OwnerToken);
        Assert.True(doc.ExpiryDateTime > DateTime.UtcNow);
    }

    [Fact]
    public async Task ConcurrentAcquire_SecondInstanceWaitsThenAcquiresAfterRelease()
    {
        var lockId = NewLockId("test_concurrent");

        var first = CreateLockService(lockId);
        var second = CreateLockService(lockId);

        try
        {
            await first.AcquireLockAsync(Ct);

            var secondTask = Task.Run(async () => await second.AcquireLockAsync(Ct), Ct);

            // Let the second instance enter its polling loop.
            await Task.Delay(1500, Ct);
            Assert.False(secondTask.IsCompleted, "Second acquire must block while first holds the lock");

            // Release the first lock — second must acquire within one poll cycle (~1s) plus margin.
            await first.DisposeAsync();
            first = null!; // prevent double-dispose in finally

            var completion = await Task.WhenAny(secondTask, Task.Delay(TimeSpan.FromSeconds(5), Ct));
            Assert.Same(secondTask, completion);
            await secondTask; // surface any exception

            var doc = await GetSysLockCollection().Find(s => s.Id == lockId).FirstOrDefaultAsync(Ct);
            Assert.NotNull(doc); // second instance now holds it
        }
        finally
        {
            if (first is not null) await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task Dispose_DoesNotDeleteLockOwnedByAnotherInstance()
    {
        var lockId = NewLockId("test_dispose_protect");
        var collection = GetSysLockCollection();

        var lockService = CreateLockService(lockId);
        await lockService.AcquireLockAsync(Ct);

        // Simulate another service stealing the lock: overwrite OwnerToken in place.
        // (No heartbeat has fired yet — _lockLost stays false, exercising the owner-scoped
        // delete filter rather than the _lockLost short-circuit.)
        var thiefToken = Guid.NewGuid();
        await collection.UpdateOneAsync(
            Builders<SysLock>.Filter.Eq(s => s.Id, lockId),
            Builders<SysLock>.Update
                .Set(s => s.OwnerToken, thiefToken)
                .Set(s => s.HolderInfo, "test/thief"),
            cancellationToken: Ct);

        await lockService.DisposeAsync();

        var doc = await collection.Find(s => s.Id == lockId).FirstOrDefaultAsync(Ct);
        Assert.NotNull(doc); // dispose's owner-scoped filter did not match — entry preserved
        Assert.Equal(thiefToken, doc.OwnerToken);
        Assert.Equal("test/thief", doc.HolderInfo);

        // Cleanup so the doc doesn't linger.
        await collection.DeleteOneAsync(Builders<SysLock>.Filter.Eq(s => s.Id, lockId), Ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task Heartbeat_DetectsStolenLockAndSignalsLockLostToken()
    {
        var lockId = NewLockId("test_heartbeat_loss");
        var collection = GetSysLockCollection();

        var lockService = CreateLockService(lockId);
        try
        {
            await lockService.AcquireLockAsync(Ct);

            // Steal the lock immediately. The next heartbeat (≤ HeartbeatInterval = 15s away)
            // must observe MatchedCount == 0 and cancel LockLostToken.
            var thiefToken = Guid.NewGuid();
            await collection.UpdateOneAsync(
                Builders<SysLock>.Filter.Eq(s => s.Id, lockId),
                Builders<SysLock>.Update.Set(s => s.OwnerToken, thiefToken),
                cancellationToken: Ct);

            var tcs = new TaskCompletionSource<bool>();
            await using var reg = lockService.LockLostToken.Register(() => tcs.TrySetResult(true));

            // Allow at least one heartbeat interval to elapse, plus generous margin.
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(25), Ct));
            Assert.Same(tcs.Task, winner);
            Assert.True(lockService.LockLostToken.IsCancellationRequested);

            // Dispose now must skip the delete (lock-lost short-circuit) — the thief's
            // entry must remain intact.
            await lockService.DisposeAsync();
            lockService = null!;

            var doc = await collection.Find(s => s.Id == lockId).FirstOrDefaultAsync(Ct);
            Assert.NotNull(doc);
            Assert.Equal(thiefToken, doc.OwnerToken);
        }
        finally
        {
            if (lockService is not null) await lockService.DisposeAsync();
            await collection.DeleteOneAsync(Builders<SysLock>.Filter.Eq(s => s.Id, lockId), Ct);
        }
    }

    [Fact]
    public async Task Acquire_HonorsCancellationTokenWhilePolling()
    {
        var lockId = NewLockId("test_cancel");

        await using var holder = CreateLockService(lockId);
        await holder.AcquireLockAsync(Ct);

        await using var contender = CreateLockService(lockId);
        using var cts = new CancellationTokenSource();

        var acquireTask = contender.AcquireLockAsync(cts.Token);

        // Let the contender start polling, then cancel.
        await Task.Delay(500, Ct);
        Assert.False(acquireTask.IsCompleted);

        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquireTask);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"Cancellation must interrupt Task.Delay promptly, took {sw.ElapsedMilliseconds}ms");
    }
}
