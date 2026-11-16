using FakeItEasy;

using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
///     The cached indexes a page hint is chosen from (AB#9138): a list is reused within the interval, and an
///     invalidation by this process is never undone by a list that was read across it.
/// </summary>
public class CollectionIndexCacheTests
{
    [Fact]
    public async Task AListIsReusedUntilTheCollectionIsInvalidated()
    {
        var lists = 0;
        var collection = FakeCollection(() =>
        {
            lists++;
            return [Index($"v{lists}")];
        });

        Assert.Equal("v1", Name(await GetAsync(collection)));
        Assert.Equal("v1", Name(await GetAsync(collection)));
        Assert.Equal(1, lists);

        CollectionIndexCache.Invalidate(collection);

        Assert.Equal("v2", Name(await GetAsync(collection)));
        Assert.Equal(2, lists);
    }

    [Fact]
    public async Task AListReadAcrossAnInvalidation_IsNeverReused()
    {
        var lists = 0;
        IMongoCollection<BsonDocument>? self = null;
        var collection = FakeCollection(() =>
        {
            lists++;
            if (lists == 1)
            {
                // Another index change of this process lands while the first list is on its way.
                CollectionIndexCache.Invalidate(self!);
            }

            return [Index($"v{lists}")];
        });
        self = collection;

        // The caller still gets what it read, but the next call does not reuse it.
        Assert.Equal("v1", Name(await GetAsync(collection)));
        Assert.Equal("v2", Name(await GetAsync(collection)));
        Assert.Equal("v2", Name(await GetAsync(collection)));
        Assert.Equal(2, lists);
    }

    private static Task<IReadOnlyList<BsonDocument>?> GetAsync(IMongoCollection<BsonDocument> collection) =>
        CollectionIndexCache.GetAsync(collection, TestContext.Current.CancellationToken);

    private static BsonDocument Index(string name) =>
        new() { { "name", name }, { "key", new BsonDocument { { "ckTypeId", 1 }, { "_id", 1 } } } };

    private static string? Name(IReadOnlyList<BsonDocument>? indexes) => indexes?.Single()["name"].AsString;

    /// <summary>
    ///     A collection with a namespace of its own (the cache is process-wide) whose <c>listIndexes</c> runs
    ///     <paramref name="list" />.
    /// </summary>
    private static IMongoCollection<BsonDocument> FakeCollection(Func<List<BsonDocument>> list)
    {
        var indexes = A.Fake<IMongoIndexManager<BsonDocument>>();
        A.CallTo(() => indexes.ListAsync(A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult<IAsyncCursor<BsonDocument>>(new ListCursor(list())));
        var collection = A.Fake<IMongoCollection<BsonDocument>>();
        A.CallTo(() => collection.CollectionNamespace)
            .Returns(new CollectionNamespace("cache_test", Guid.NewGuid().ToString("N")));
        A.CallTo(() => collection.Indexes).Returns(indexes);
        return collection;
    }

    private sealed class ListCursor(List<BsonDocument> documents) : IAsyncCursor<BsonDocument>
    {
        private bool _moved;

        public IEnumerable<BsonDocument> Current => documents;

        public bool MoveNext(CancellationToken cancellationToken = default)
        {
            var first = !_moved;
            _moved = true;
            return first;
        }

        public Task<bool> MoveNextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(MoveNext(cancellationToken));

        public void Dispose()
        {
        }
    }
}
