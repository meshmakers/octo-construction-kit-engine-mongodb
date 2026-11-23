using FakeItEasy;

using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging.Abstractions;

using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
///     AB#9302 (CodeRabbit round 2 on PR #275): the acquire never takes the lock after its deadline, and it never reports
///     success when the heartbeat found the lock taken by another holder while the acquisition was completed.
/// </summary>
public class RepositoryDistributedLockServiceDeadlineTests
{
    private const string LockId = "index_update_lock";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     The waiter-marker write outlasts the whole wait, so the delay before the next poll is zero and the next poll
    ///     would start after the deadline - with the lock free by then. The acquire times out instead of taking it.
    /// </summary>
    [Fact]
    public async Task Acquire_NeverPollsAfterTheDeadline_EvenWhenTheLockIsFreeByThen()
    {
        var collection = FakeLockCollection();
        var mongoCollection = A.Fake<IMongoCollection<SysLock>>();
        A.CallTo(() => collection.GetMongoCollection()).Returns(mongoCollection);

        // The marker write takes longer than maxWait.
        A.CallTo(() => mongoCollection.ReplaceOneAsync(A<FilterDefinition<SysLock>>._, A<SysLock>._,
                A<ReplaceOptions>._, A<CancellationToken>._))
            .ReturnsLazily(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), Ct);
                return (ReplaceOneResult)new ReplaceOneResult.Acknowledged(1, 1, null);
            });

        var (client, repository) = CreateRepository(collection);
        await using var lockService = new RepositoryDistributedLockService(client, repository, NullLogger.Instance,
            LockId, markWaiters: true);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            lockService.AcquireLockAsync(TimeSpan.FromMilliseconds(100), Ct));

        // One read only (the held lock); no poll after the deadline, so no insert of the free lock.
        A.CallTo(() => collection.FindManyAsync(A<IOctoSession>._, A<FilterDefinition<SysLock>>._,
                A<SortDefinition<SysLock>?>._, A<int?>._, A<int?>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => collection.InsertOneAsync(A<IOctoSession>._, A<SysLock>._)).MustNotHaveHappened();
        Assert.False(lockService.LockLostToken.IsCancellationRequested);
    }

    /// <summary>
    ///     The contender waited (marker written), then took the lock; while its marker cleanup runs, the heartbeat finds
    ///     the lock document owned by another holder. The acquire throws with the cancelled LockLostToken instead of
    ///     returning success, and the dispose leaves the other holder's lock document alone.
    /// </summary>
    [Fact]
    public async Task Acquire_ThrowsWhenTheLockIsLostWhileTheWaiterMarkerIsCleanedUp()
    {
        var collection = FakeLockCollection();
        var mongoCollection = A.Fake<IMongoCollection<SysLock>>();
        A.CallTo(() => collection.GetMongoCollection()).Returns(mongoCollection);
        A.CallTo(() => mongoCollection.ReplaceOneAsync(A<FilterDefinition<SysLock>>._, A<SysLock>._,
                A<ReplaceOptions>._, A<CancellationToken>._))
            .Returns(Task.FromResult<ReplaceOneResult>(new ReplaceOneResult.Acknowledged(1, 1, null)));

        // The heartbeat's owner-scoped update matches nothing: another holder owns the lock now.
        A.CallTo(() => mongoCollection.UpdateOneAsync(A<IClientSessionHandle>._, A<FilterDefinition<SysLock>>._,
                A<UpdateDefinition<SysLock>>._, A<UpdateOptions>._, A<CancellationToken>._))
            .Returns(Task.FromResult<UpdateResult>(new UpdateResult.Acknowledged(0, 0, null)));

        // The marker cleanup is still running when the heartbeat flags the loss: it ends only once LockLostToken is
        // cancelled (or its own bound of 5 s is over, which fails the test below).
        RepositoryDistributedLockService? lockService = null;
        var cleanupSawLoss = false;
        A.CallTo(() => mongoCollection.DeleteOneAsync(A<FilterDefinition<SysLock>>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                using var both = CancellationTokenSource.CreateLinkedTokenSource(lockService!.LockLostToken,
                    call.GetArgument<CancellationToken>(1));
                try
                {
                    await Task.Delay(Timeout.Infinite, both.Token);
                }
                catch (OperationCanceledException)
                {
                    cleanupSawLoss = lockService.LockLostToken.IsCancellationRequested;
                }

                return (DeleteResult)new DeleteResult.Acknowledged(1);
            });

        var (client, repository) = CreateRepository(collection);
        lockService = new RepositoryDistributedLockService(client, repository, NullLogger.Instance, LockId,
            markWaiters: true) { HeartbeatInterval = TimeSpan.FromMilliseconds(20) };

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lockService.AcquireLockAsync(TimeSpan.FromSeconds(30), Ct));
        Assert.True(cleanupSawLoss, "the heartbeat did not flag the loss while the marker cleanup ran");
        Assert.True(lockService.LockLostToken.IsCancellationRequested);
        Assert.Equal(lockService.LockLostToken, thrown.CancellationToken);

        await lockService.DisposeAsync();

        // The release never deletes the lock document (the other holder's); only the waiter marker was deleted.
        A.CallTo(() => collection.DeleteOneAsync(A<IOctoSession>._, A<FilterDefinition<SysLock>>._))
            .MustNotHaveHappened();
    }

    /// <summary>The lock is held by another holder on the first read and free on every later read.</summary>
    private static IMongoDbDataSourceCollection<string, SysLock> FakeLockCollection()
    {
        var collection = A.Fake<IMongoDbDataSourceCollection<string, SysLock>>();
        var reads = 0;
        A.CallTo(() => collection.FindManyAsync(A<IOctoSession>._, A<FilterDefinition<SysLock>>._,
                A<SortDefinition<SysLock>?>._, A<int?>._, A<int?>._))
            .ReturnsLazily(() =>
            {
                var held = Interlocked.Increment(ref reads) == 1;
                ICollection<SysLock> result = held
                    ?
                    [
                        new SysLock
                        {
                            Id = LockId,
                            CreationDateTime = DateTime.UtcNow,
                            ExpiryDateTime = DateTime.UtcNow.AddMinutes(1),
                            LastHeartbeat = DateTime.UtcNow,
                            OwnerToken = Guid.NewGuid(),
                            HolderInfo = "other"
                        }
                    ]
                    : [];
                return Task.FromResult(result);
            });
        A.CallTo(() => collection.InsertOneAsync(A<IOctoSession>._, A<SysLock>._)).Returns(Task.CompletedTask);
        return collection;
    }

    private static (IRepositoryClient Client, IRepositoryInternal Repository) CreateRepository(
        IMongoDbDataSourceCollection<string, SysLock> collection)
    {
        var repository = A.Fake<IRepositoryInternal>();
        A.CallTo(() => repository.GetCollection(A<IMongoDataSourceMapper<string, SysLock>>._, A<string?>._))
            .Returns(collection);

        // The heartbeat uses the driver session of the Octo session.
        var client = A.Fake<IRepositoryClient>();
        A.CallTo(() => client.GetSessionAsync()).ReturnsLazily(() =>
            Task.FromResult(A.Fake<IOctoSession>(o => o.Implements<IOctoSessionInternal>())));
        return (client, repository);
    }
}
