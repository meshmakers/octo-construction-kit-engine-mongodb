using System.Net;
using System.Reflection;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
///     AB#8020: a single-document write whose write concern failed (no write error, only a write-concern error such as the
///     <c>wtimeout</c> of <c>w:majority</c>) must keep the driver exception as its cause, so that the method host can answer
///     <c>METHOD_OUTCOME_UNKNOWN</c>. Before, the mapping dereferenced the missing write error and threw a
///     <see cref="NullReferenceException" /> that lost the cause.
/// </summary>
public class MongoDbWriteExceptionMappingTests
{
    private static readonly ConnectionId Connection =
        new(new ServerId(new ClusterId(), new DnsEndPoint("octo-mongodb-0", 27017)));

    private static Exception Map(MongoWriteException ex) =>
        MongoDbDataSourceCollection<ObjectId, BsonDocument>.CreateWriteException<BsonDocument>("UpdateOneAsync", ex);

    [Fact]
    public void WriteConcernTimeoutOnly_IsAnOperationFailure_ThatKeepsTheDriverException()
    {
        var timeout = new MongoWriteException(Connection, null,
            Create<WriteConcernError>(64, "WriteConcernTimeout", "waiting for replication timed out", new BsonDocument(),
                Array.Empty<string>()), null);

        var mapped = Map(timeout);

        Assert.IsType<OperationFailedException>(mapped);
        Assert.Same(timeout, mapped.InnerException);
        Assert.NotNull(((MongoWriteException)mapped.InnerException!).WriteConcernError);
    }

    [Fact]
    public void OtherWriteError_IsAnOperationFailure_AsBefore()
    {
        var refused = new MongoWriteException(Connection,
            Create<WriteError>(ServerErrorCategory.Uncategorized, 2, "bad value", new BsonDocument()), null, null);

        var mapped = Map(refused);

        Assert.IsType<OperationFailedException>(mapped);
        Assert.Same(refused, mapped.InnerException);
    }

    [Fact]
    public void DuplicateKey_IsStillADuplicateKeyException()
    {
        var duplicate = new MongoWriteException(Connection,
            Create<WriteError>(ServerErrorCategory.DuplicateKey, 11000, "E11000 duplicate key error", new BsonDocument()),
            null, null);

        // DuplicateKeyException.DuplicateKeyError throws instead of returning (existing behaviour)
        var thrown = Assert.Throws<DuplicateKeyException>(() => Map(duplicate));
        Assert.Same(duplicate, thrown.InnerException);
    }

    // WriteConcernError and WriteError have no public constructor
    private static T Create<T>(params object?[] arguments)
    {
        var constructor = typeof(T).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(c => c.GetParameters().Length == arguments.Length);
        return (T)constructor.Invoke(arguments);
    }
}
