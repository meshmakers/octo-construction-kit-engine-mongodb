using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
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
}
