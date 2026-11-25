using System.Net;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.AutoIncrement;

/// <summary>
///     AB#9753/AB#9755 without a database: the decisions of the auto-increment allocation - when a failed statement
///     runs again, which numbers a block holds, when a block cannot fit, and how many numbers the end-reached error
///     reports. The integration tests prove the statements against MongoDB; these pin the rules on their own.
/// </summary>
public class AutoIncrementAllocationRulesTests
{
    [Theory]
    // Outside a transaction a transient error is retried until the last of the attempts.
    [InlineData(false, 1, true)]
    [InlineData(false, 2, true)]
    [InlineData(false, 3, false)]
    // Inside a transaction never: the server aborted it, only its owner can run it again.
    [InlineData(true, 1, false)]
    public void TransientError_IsRetried_OnlyOutsideATransaction_AndAtMostThreeAttempts(bool inTransaction,
        int attempt, bool expected)
    {
        Assert.Equal(3, AutoIncrementModifier.MaxAllocationAttempts);
        Assert.Equal(expected,
            AutoIncrementModifier.ShouldRetryAllocation(Transient(), inTransaction, attempt));
    }

    [Fact]
    public void OtherErrors_AreNeverRetried()
    {
        Assert.False(AutoIncrementModifier.ShouldRetryAllocation(
            CommandException(13, "Unauthorized", transientLabel: false), inTransaction: false, attempt: 1));
        Assert.False(AutoIncrementModifier.ShouldRetryAllocation(
            new InvalidOperationException("not a database error"), inTransaction: false, attempt: 1));
    }

    [Theory]
    [InlineData(42L, 1, 42L)]
    [InlineData(47L, 5, 43L)]
    [InlineData(1L, 1, 1L)]
    public void Block_StartsRightAfterTheCounterBeforeTheStatement(long counterAfter, int count, long first)
    {
        Assert.Equal(first, AutoIncrementModifier.FirstNumberOfTheBlock(counterAfter, count));
    }

    [Theory]
    [InlineData(1L, 5, true)]
    [InlineData(long.MaxValue, 1, true)]
    [InlineData(long.MaxValue, 2, false)]
    [InlineData(long.MaxValue - 4, 5, true)]
    [InlineData(long.MaxValue - 4, 6, false)]
    public void Block_ThatWouldPassLongMaxValue_DoesNotFit(long first, int count, bool fits)
    {
        Assert.Equal(fits, AutoIncrementModifier.BlockFitsInALong(first, count));
    }

    [Theory]
    // Without a start value: End - counter.
    [InlineData(40L, 50L, null, 10L)]
    // The StartValue floor: max(counter, StartValue - 1).
    [InlineData(0L, 100L, 100L, 1L)]
    [InlineData(0L, 100L, 1L, 100L)]
    // A start value below the counter is ignored.
    [InlineData(60L, 100L, 10L, 40L)]
    // Never negative.
    [InlineData(120L, 100L, null, 0L)]
    public void EndReached_ReportsTheNumbersLeft_FromTheStartValueFloor(long current, long end, long? startValue,
        long left)
    {
        var withoutYear = AutoIncrementFailedException.AutoIncrementEndReached(OctoObjectId.GenerateNewId(),
            "InvoiceNumber", current, end, 200, startValue);
        var withYear = AutoIncrementFailedException.AutoIncrementEndReached(OctoObjectId.GenerateNewId(),
            "InvoiceNumber", 2026, current, end, 200, startValue);

        Assert.Contains($"{left} left up to End {end}", withoutYear.Message);
        Assert.Contains($"{left} left up to End {end}", withYear.Message);
        Assert.Contains("no number of it was taken", withoutYear.Message);
    }

    private static MongoCommandException Transient() => CommandException(112, "WriteConflict", transientLabel: true);

    private static MongoCommandException CommandException(int code, string codeName, bool transientLabel)
    {
        var connectionId = new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
        var result = new BsonDocument { { "ok", 0 }, { "code", code }, { "codeName", codeName }, { "errmsg", "test" } };
        var exception = new MongoCommandException(connectionId, "test", new BsonDocument("findAndModify", "c"), result);
        if (transientLabel)
        {
            exception.AddErrorLabel("TransientTransactionError");
        }

        return exception;
    }
}
