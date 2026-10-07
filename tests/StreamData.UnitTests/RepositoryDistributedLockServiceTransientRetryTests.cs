using System.Net;

using FakeItEasy;

using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging.Abstractions;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#5533: a WriteConflict (code 112, label TransientTransactionError) while inserting the lock
/// document must be retried inside the acquire loop instead of failing the whole acquire.
/// </summary>
public class RepositoryDistributedLockServiceTransientRetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Acquire_RetriesAfterTransientWriteConflictOnInsert()
    {
        var collection = A.Fake<IMongoDbDataSourceCollection<string, SysLock>>();
        A.CallTo(() => collection.FindManyAsync(A<IOctoSession>._, A<FilterDefinition<SysLock>>._,
                A<SortDefinition<SysLock>?>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult<ICollection<SysLock>>(new List<SysLock>()));

        // First insert fails like the data-source collection reports it: a WriteConflict
        // command error wrapped in OperationFailedException. The second insert succeeds.
        A.CallTo(() => collection.InsertOneAsync(A<IOctoSession>._, A<SysLock>._))
            .Throws(OperationFailedException.DatabaseOperationFailed("InsertOneAsync", CreateWriteConflict()))
            .Once()
            .Then
            .Returns(Task.CompletedTask);

        var (client, repository) = CreateRepository(collection);
        await using var lockService =
            new RepositoryDistributedLockService(client, repository, NullLogger.Instance, "index_update_lock");

        await lockService.AcquireLockAsync(Ct);

        A.CallTo(() => collection.InsertOneAsync(A<IOctoSession>._, A<SysLock>._))
            .MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task Acquire_DoesNotRetryNonTransientErrors()
    {
        var collection = A.Fake<IMongoDbDataSourceCollection<string, SysLock>>();
        A.CallTo(() => collection.FindManyAsync(A<IOctoSession>._, A<FilterDefinition<SysLock>>._,
                A<SortDefinition<SysLock>?>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult<ICollection<SysLock>>(new List<SysLock>()));
        A.CallTo(() => collection.InsertOneAsync(A<IOctoSession>._, A<SysLock>._))
            .Throws(OperationFailedException.DatabaseOperationFailed("InsertOneAsync",
                CreateCommandException(code: 13, codeName: "Unauthorized", transientLabel: false)));

        var (client, repository) = CreateRepository(collection);
        await using var lockService =
            new RepositoryDistributedLockService(client, repository, NullLogger.Instance, "index_update_lock");

        await Assert.ThrowsAsync<OperationFailedException>(() => lockService.AcquireLockAsync(Ct));
        A.CallTo(() => collection.InsertOneAsync(A<IOctoSession>._, A<SysLock>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void IsTransientTransactionError_RecognisesWriteConflictAndLabel()
    {
        Assert.True(RepositoryDistributedLockService.IsTransientTransactionError(CreateWriteConflict()));
        Assert.True(RepositoryDistributedLockService.IsTransientTransactionError(
            OperationFailedException.DatabaseOperationFailed("op", CreateWriteConflict())));
        Assert.True(RepositoryDistributedLockService.IsTransientTransactionError(
            CreateCommandException(code: 251, codeName: "NoSuchTransaction", transientLabel: true)));
        Assert.True(RepositoryDistributedLockService.IsTransientTransactionError(
            CreateCommandException(code: 112, codeName: "WriteConflict", transientLabel: false)));

        Assert.False(RepositoryDistributedLockService.IsTransientTransactionError(
            CreateCommandException(code: 13, codeName: "Unauthorized", transientLabel: false)));
        Assert.False(RepositoryDistributedLockService.IsTransientTransactionError(
            new InvalidOperationException("not a mongo error")));
    }

    private static (IRepositoryClient Client, IRepositoryInternal Repository) CreateRepository(
        IMongoDbDataSourceCollection<string, SysLock> collection)
    {
        var repository = A.Fake<IRepositoryInternal>();
        A.CallTo(() => repository.GetCollection(A<IMongoDataSourceMapper<string, SysLock>>._, A<string?>._))
            .Returns(collection);

        var client = A.Fake<IRepositoryClient>();
        A.CallTo(() => client.GetSessionAsync()).ReturnsLazily(() => Task.FromResult(A.Fake<IOctoSession>()));
        return (client, repository);
    }

    private static MongoCommandException CreateWriteConflict() =>
        CreateCommandException(code: 112, codeName: "WriteConflict", transientLabel: true);

    private static MongoCommandException CreateCommandException(int code, string codeName, bool transientLabel)
    {
        var connectionId = new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
        var result = new BsonDocument
        {
            { "ok", 0 },
            { "code", code },
            { "codeName", codeName },
            { "errmsg", "test error" }
        };
        var exception = new MongoCommandException(connectionId, "test error", new BsonDocument("insert", "sys_locks"),
            result);
        if (transientLabel)
        {
            exception.AddErrorLabel("TransientTransactionError");
        }

        return exception;
    }
}
