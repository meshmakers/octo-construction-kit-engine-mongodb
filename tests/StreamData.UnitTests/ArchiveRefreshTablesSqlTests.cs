using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#8735: the archive stats refresh the listed tables in one statement before they read <c>sys.shards.num_docs</c>,
/// which lags behind on a shard CrateDB has not refreshed.
/// </summary>
public class ArchiveRefreshTablesSqlTests
{
    [Fact]
    public void BuildRefreshTablesSql_RefreshesEveryTableInOneStatement()
    {
        var sql = CrateDatabaseClient.BuildRefreshTablesSql("acme", ["archive_aa", "archive_bb"]);

        Assert.Equal("REFRESH TABLE \"acme\".\"archive_aa\", \"acme\".\"archive_bb\"", sql);
    }

    [Fact]
    public void BuildRefreshTablesSql_LeavesOutANameThatIsNoPlainIdentifier()
    {
        var sql = CrateDatabaseClient.BuildRefreshTablesSql("acme", ["archive_aa", "x\" ; DROP TABLE y; --", "Archive_Upper"]);

        Assert.Equal("REFRESH TABLE \"acme\".\"archive_aa\"", sql);
    }

    [Fact]
    public void BuildRefreshTablesSql_NoTable_IsNull()
    {
        Assert.Null(CrateDatabaseClient.BuildRefreshTablesSql("acme", []));
    }

    [Fact]
    public void IsRefreshFallback_ServerError_FallsBack()
    {
        var ex = new PostgresException("relation unknown", "ERROR", "ERROR", "42P01");

        Assert.True(CrateDatabaseClient.IsRefreshFallback(ex, CancellationToken.None));
    }

    [Fact]
    public void IsRefreshFallback_DriverCommandTimeout_FallsBack()
    {
        var ex = new NpgsqlException("Exception while reading from stream", new TimeoutException("Timeout during reading attempt"));

        Assert.True(CrateDatabaseClient.IsRefreshFallback(ex, CancellationToken.None));
    }

    [Fact]
    public void IsRefreshFallback_CallBudgetExpired_FallsBack()
    {
        Assert.True(CrateDatabaseClient.IsRefreshFallback(new OperationCanceledException(), CancellationToken.None));
    }

    [Fact]
    public void IsRefreshFallback_CallerCancelled_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.False(CrateDatabaseClient.IsRefreshFallback(new OperationCanceledException(cts.Token), cts.Token));
        Assert.False(CrateDatabaseClient.IsRefreshFallback(
            new NpgsqlException("Exception while reading from stream", new TimeoutException()), cts.Token));
    }

    [Fact]
    public void IsRefreshFallback_BrokenConnection_IsLeftToTheResiliencePipeline()
    {
        var ex = new NpgsqlException("Exception while reading from stream", new EndOfStreamException());

        Assert.False(CrateDatabaseClient.IsRefreshFallback(ex, CancellationToken.None));
    }
}
