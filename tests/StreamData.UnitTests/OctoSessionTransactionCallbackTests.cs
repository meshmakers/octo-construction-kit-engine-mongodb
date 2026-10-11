using FakeItEasy;

using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using Microsoft.Extensions.Logging.Abstractions;

using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
///     AB#6248: the after-commit / after-rollback callbacks of <see cref="OctoSession" /> (GridFS clean-up). The driver
///     session is faked so that commit and abort can be made to fail.
///     Invariant: staged bytes are removed whenever the transaction is known not to have committed; they are KEPT
///     (possible orphans) whenever a commit was attempted, because its outcome may be "committed".
/// </summary>
public class OctoSessionTransactionCallbackTests
{
    private readonly IClientSessionHandle _handle = A.Fake<IClientSessionHandle>();
    private readonly OctoAdminSession _session;
    private int _afterCommit;
    private int _afterRollback;

    public OctoSessionTransactionCallbackTests()
    {
        _session = new OctoAdminSession(NullLogger<OctoAdminSession>.Instance, _handle, "test");
        _session.StartTransaction();
        _session.RegisterTransactionCallbacks(() => { _afterCommit++; return Task.CompletedTask; },
            () => { _afterRollback++; return Task.CompletedTask; });
    }

    [Fact]
    public async Task Commit_RunsOnlyTheAfterCommitCallbacks()
    {
        await _session.CommitTransactionAsync();
        _session.Dispose();

        Assert.Equal(1, _afterCommit);
        Assert.Equal(0, _afterRollback);
    }

    [Fact]
    public async Task Abort_RunsOnlyTheRollbackCallbacks()
    {
        await _session.AbortTransactionAsync();

        Assert.Equal(0, _afterCommit);
        Assert.Equal(1, _afterRollback);
    }

    [Fact]
    public async Task Abort_ThatThrows_StillRunsTheRollbackCallbacks()
    {
        A.CallTo(() => _handle.AbortTransactionAsync(A<CancellationToken>._)).Throws(new IOException("abort failed"));

        await Assert.ThrowsAsync<IOException>(() => _session.AbortTransactionAsync());

        Assert.Equal(1, _afterRollback);
        Assert.Equal(0, _afterCommit);
    }

    [Fact]
    public void Dispose_WithoutCommit_WhereTheAbortThrows_StillRunsTheRollbackCallbacks()
    {
        A.CallTo(() => _handle.AbortTransaction(A<CancellationToken>._)).Throws(new IOException("abort failed"));

        _session.Dispose();

        Assert.Equal(1, _afterRollback);
        Assert.Equal(0, _afterCommit);
    }

    [Fact]
    public async Task Commit_ThatThrows_RunsNeitherCallbackSet_EvenWhenTheSessionIsAbortedAndDisposedAfterwards()
    {
        // Unknown result: the commit may have reached the server. Deleting the staged bytes could then delete bytes of
        // a committed entity, so they are kept as (documented) orphans instead.
        A.CallTo(() => _handle.CommitTransactionAsync(A<CancellationToken>._)).Throws(new IOException("commit failed"));
        A.CallTo(() => _handle.AbortTransaction(A<CancellationToken>._)).Throws(new InvalidOperationException("after commit"));

        await Assert.ThrowsAsync<IOException>(() => _session.CommitTransactionAsync());
        _session.Dispose();

        Assert.Equal(0, _afterCommit);
        Assert.Equal(0, _afterRollback);
    }

    [Fact]
    public async Task Callbacks_RunOnce_AndFailuresAreSwallowed()
    {
        _session.RegisterTransactionCallbacks(() => throw new IOException("delete failed"));

        await _session.CommitTransactionAsync();

        Assert.Equal(1, _afterCommit);
    }
}
