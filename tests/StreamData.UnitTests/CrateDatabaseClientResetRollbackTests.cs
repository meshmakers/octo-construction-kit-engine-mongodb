using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// The ROLLBACK that Npgsql sends when it resets a connection is rejected by CrateDB. 5.x answers with
/// <c>XX000</c>, 6.x with the syntax error <c>42601</c> (AB#7138). Only that rejection is benign; every other
/// error of the same SQL state must still surface.
/// </summary>
public class CrateDatabaseClientResetRollbackTests
{
    [Theory]
    [InlineData("XX000", "line 1:1: mismatched input 'ROLLBACK' expecting {<EOF>, 'SELECT'}")]
    [InlineData("42601", "line 1:1: extraneous input 'ROLLBACK' expecting {<EOF>, 'SELECT', 'DEALLOCATE'}")]
    public void TheRejectedResetRollback_IsBenign(string sqlState, string message) =>
        Assert.True(CrateDatabaseClient.IsBenignCrateResetRollback(sqlState, message));

    [Theory]
    [InlineData("42601", "line 1:8: mismatched input 'FROM' expecting {<EOF>}")]
    [InlineData("42601", "line 1:1: extraneous input 'COMMIT' expecting {<EOF>}")]
    [InlineData("42P01", "Relation 'doc.t' unknown")]
    [InlineData("XX000", "Job killed")]
    [InlineData(null, "extraneous input 'ROLLBACK'")]
    [InlineData("42601", null)]
    public void AnyOtherError_StillSurfaces(string? sqlState, string? message) =>
        Assert.False(CrateDatabaseClient.IsBenignCrateResetRollback(sqlState, message));
}
