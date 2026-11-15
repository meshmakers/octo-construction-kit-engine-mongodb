using System.Diagnostics;
using System.Linq.Expressions;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Engine.Resolvers.Repository;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests;

/// <summary>
///     AB#8947: the dependency resolve asks the lock collection for running CK model imports at most once per tenant
///     database and TTL, and at once again after an import lock of this process changed. The detection across processes
///     against a real lock collection is covered by <c>CkModelImportLockCacheIntegrationTests</c>.
/// </summary>
public class CkModelImportLockCacheTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(2);

    private readonly ManualTimeProvider _clock = new();
    private int _lockQueries;

    private ICkMongoDbRepositoryDataSource DataSource(params string[] running)
    {
        var dataSource = A.Fake<ICkMongoDbRepositoryDataSource>();
        A.CallTo(() => dataSource.GetModelsBeingImportedAsync(A<CancellationToken>._))
            .Invokes(() => Interlocked.Increment(ref _lockQueries))
            .Returns(Task.FromResult<IReadOnlyList<string>>(running));
        return dataSource;
    }

    [Fact]
    public async Task RepeatedReads_WithinTheTtl_QueryTheLockCollectionOnce()
    {
        var cache = new CkModelImportLockCache(Ttl, _clock);
        var dataSource = DataSource();

        for (var i = 0; i < 100; i++)
        {
            Assert.Empty(await cache.GetModelsBeingImportedAsync(dataSource, TestContext.Current.CancellationToken));
            _clock.Advance(TimeSpan.FromMilliseconds(19));
        }

        Assert.Equal(1, _lockQueries);
    }

    [Fact]
    public async Task ReadAfterTheTtl_QueriesAgain()
    {
        var cache = new CkModelImportLockCache(Ttl, _clock);
        var dataSource = DataSource("Basic");

        Assert.Equal(["Basic"], await cache.GetModelsBeingImportedAsync(dataSource, TestContext.Current.CancellationToken));
        _clock.Advance(Ttl - TimeSpan.FromMilliseconds(1));
        await cache.GetModelsBeingImportedAsync(dataSource, TestContext.Current.CancellationToken);
        Assert.Equal(1, _lockQueries);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await cache.GetModelsBeingImportedAsync(dataSource, TestContext.Current.CancellationToken);
        Assert.Equal(2, _lockQueries);
    }

    [Fact]
    public async Task ImportLockChangedInThisProcess_TheNextReadQueriesAtOnce()
    {
        var cache = new CkModelImportLockCache(Ttl, _clock);
        var dataSource = DataSource();

        await cache.GetModelsBeingImportedAsync(dataSource, TestContext.Current.CancellationToken);
        cache.NotifyImportLockChanged(dataSource);
        await cache.GetModelsBeingImportedAsync(dataSource, TestContext.Current.CancellationToken);
        await cache.GetModelsBeingImportedAsync(dataSource, TestContext.Current.CancellationToken);

        Assert.Equal(2, _lockQueries);
    }

    [Fact]
    public async Task ImportLockChangesWhileTheQueryRuns_ItsAnswerIsNotReused()
    {
        var cache = new CkModelImportLockCache(Ttl, _clock);
        var dataSource = A.Fake<ICkMongoDbRepositoryDataSource>();
        var answers = new Queue<IReadOnlyList<string>>([[], ["Basic"]]);
        A.CallTo(() => dataSource.GetModelsBeingImportedAsync(A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                _lockQueries++;
                var answer = answers.Dequeue();
                // The lock is acquired in this process after the query read the collection, before it returned.
                if (_lockQueries == 1)
                {
                    cache.NotifyImportLockChanged(dataSource);
                }

                return Task.FromResult(answer);
            });

        Assert.Empty(await cache.GetModelsBeingImportedAsync(dataSource, TestContext.Current.CancellationToken));
        Assert.Equal(["Basic"], await cache.GetModelsBeingImportedAsync(dataSource, TestContext.Current.CancellationToken));
        Assert.Equal(2, _lockQueries);
    }

    [Fact]
    public async Task DataSourcesOfTheSameDatabase_ShareOneEntry_OtherDatabasesDoNot()
    {
        var cache = new CkModelImportLockCache(Ttl, _clock);
        var first = ScopedDataSource("mongo:27017/tenant_a");
        var second = ScopedDataSource("mongo:27017/tenant_a");
        var other = ScopedDataSource("mongo:27017/tenant_b");

        await cache.GetModelsBeingImportedAsync(first, TestContext.Current.CancellationToken);
        await cache.GetModelsBeingImportedAsync(second, TestContext.Current.CancellationToken);
        Assert.Equal(1, _lockQueries);

        await cache.GetModelsBeingImportedAsync(other, TestContext.Current.CancellationToken);
        Assert.Equal(2, _lockQueries);

        // A lock change through one instance invalidates the entry every instance of that database reads.
        cache.NotifyImportLockChanged(first);
        await cache.GetModelsBeingImportedAsync(second, TestContext.Current.CancellationToken);
        await cache.GetModelsBeingImportedAsync(other, TestContext.Current.CancellationToken);
        Assert.Equal(3, _lockQueries);
    }

    [Fact]
    public async Task WaitUntilForeignSnapshotsExpired_WaitsOutTheRestOfTheTtl()
    {
        var ttl = TimeSpan.FromMilliseconds(400);
        var cache = new CkModelImportLockCache(ttl, TimeProvider.System);

        var waited = Stopwatch.StartNew();
        await cache.WaitUntilForeignSnapshotsExpiredAsync(cache.UtcNow, TestContext.Current.CancellationToken);
        waited.Stop();
        Assert.True(waited.Elapsed >= ttl - TimeSpan.FromMilliseconds(20), $"waited {waited.Elapsed}");

        // The lock was acquired longer ago than one TTL: no wait.
        var noWait = Stopwatch.StartNew();
        await cache.WaitUntilForeignSnapshotsExpiredAsync(cache.UtcNow - ttl, TestContext.Current.CancellationToken);
        noWait.Stop();
        Assert.True(noWait.Elapsed < TimeSpan.FromMilliseconds(100), $"waited {noWait.Elapsed}");
    }

    /// <summary>AC 2: <c>IsExistingAsync</c> outside a transaction asks the lock collection at most once per TTL.</summary>
    [Fact]
    public async Task IsExisting_CalledRepeatedlyOutsideATransaction_QueriesTheLockCollectionOncePerTtl()
    {
        var cache = new CkModelImportLockCache(Ttl, _clock);
        var dataSource = DataSource();
        var installed = new CkModel
        {
            Id = new CkModelId("Basic-1.2.0"), ModelId = "Basic", ModelState = ModelState.Available
        };
        A.CallTo(() => dataSource.CkModels.FindManyAsync(A<IOctoSession>._,
                A<Expression<Func<CkModel, bool>>>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult<ICollection<CkModel>>([installed]));
        var repository = new DatabaseCkModelRepository(NullLogger<DatabaseCkModelRepository>.Instance,
            A.Fake<IRepositoryModelResolver>(), A.Fake<ICkModelImportAuditTrail>(), cache);
        var source = new TenantDatabaseSourceIdentifier(A.Fake<IOctoSession>(), dataSource, "tenant-x");

        for (var i = 0; i < 50; i++)
        {
            var result = await repository.IsExistingAsync(new CkModelIdVersionRange("Basic", "[1.0,2.0)"), source);
            Assert.True(result.Exists);
            Assert.Equal(installed.Id, result.ModelId);
        }

        Assert.Equal(1, _lockQueries);

        _clock.Advance(Ttl);
        await repository.IsExistingAsync(new CkModelIdVersionRange("Basic", "[1.0,2.0)"), source);
        Assert.Equal(2, _lockQueries);
    }

    private ICkMongoDbRepositoryDataSource ScopedDataSource(string scopeKey)
    {
        var dataSource = A.Fake<ICkMongoDbRepositoryDataSource>(options => options.Implements<ICkModelImportLockScope>());
        A.CallTo(() => ((ICkModelImportLockScope)dataSource).ImportLockScopeKey).Returns(scopeKey);
        A.CallTo(() => dataSource.GetModelsBeingImportedAsync(A<CancellationToken>._))
            .Invokes(() => Interlocked.Increment(ref _lockQueries))
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));
        return dataSource;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
