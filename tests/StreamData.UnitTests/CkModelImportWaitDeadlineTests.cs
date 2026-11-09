using System.Diagnostics;
using FakeItEasy;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests;

/// <summary>
///     AB#8677: a dependency resolve waits for running imports against one deadline, not a new one per lookup, and the
///     wait ends with its caller's cancellation. The waits against a real import lock are covered by
///     <c>CkRangeRetentionImportTests</c> in the integration tests.
/// </summary>
public class CkModelImportWaitDeadlineTests
{
    private static ICkMongoDbRepositoryDataSource ImportNeverEnds()
    {
        var dataSource = A.Fake<ICkMongoDbRepositoryDataSource>();
        A.CallTo(() => dataSource.GetModelsBeingImportedAsync(A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<string>>(["Basic"]));
        return dataSource;
    }

    [Fact]
    public void Start_TheFirstCallFixesTheDeadline_LaterCallsGetTheSameOne()
    {
        var deadline = new CkModelImportWaitDeadline(TimeSpan.FromMinutes(2));
        var first = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(first.AddMinutes(2), deadline.Start(first));
        Assert.Equal(first.AddMinutes(2), deadline.Start(first.AddMinutes(1)));
        Assert.Equal(first.AddMinutes(2), deadline.Start(first.AddMinutes(5)));
    }

    [Fact]
    public async Task WaitUntilFinished_SecondLookupOfTheSameResolve_DoesNotWaitAgain()
    {
        var dataSource = ImportNeverEnds();
        var deadline = new CkModelImportWaitDeadline(TimeSpan.FromMilliseconds(600));

        var first = Stopwatch.StartNew();
        Assert.False(await CkModelImportInProgress.WaitUntilFinishedAsync(dataSource, n => n == "Basic", deadline,
            TestContext.Current.CancellationToken));
        first.Stop();

        var second = Stopwatch.StartNew();
        Assert.False(await CkModelImportInProgress.WaitUntilFinishedAsync(dataSource, n => n == "Basic", deadline,
            TestContext.Current.CancellationToken));
        second.Stop();

        Assert.True(first.Elapsed >= TimeSpan.FromMilliseconds(500), $"first wait took {first.Elapsed}");
        // The budget is spent: the second lookup checks once and returns without a poll interval.
        Assert.True(second.Elapsed < TimeSpan.FromMilliseconds(200), $"second wait took {second.Elapsed}");
    }

    [Fact]
    public async Task WaitUntilFinished_CallerCancels_TheWaitEndsWithTheCancellation()
    {
        var dataSource = ImportNeverEnds();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var wait = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CkModelImportInProgress.WaitUntilFinishedAsync(dataSource, n => n == "Basic",
                new CkModelImportWaitDeadline(), cancellation.Token));
        wait.Stop();

        Assert.True(wait.Elapsed < TimeSpan.FromSeconds(10), $"the wait took {wait.Elapsed} instead of ending with the caller");
    }

    [Fact]
    public void SourceIdentifier_OneDeadlinePerIdentifier_ACopyForTheSameResolveSharesIt()
    {
        var dataSource = A.Fake<ICkMongoDbRepositoryDataSource>();
        var resolve = new TenantDatabaseSourceIdentifier(null, dataSource, "tenant");
        using var cancellation = new CancellationTokenSource();

        var sameResolve = resolve with { CancellationToken = cancellation.Token };
        var nextResolve = new TenantDatabaseSourceIdentifier(null, dataSource, "tenant");

        Assert.Same(resolve.ImportWaitDeadline, sameResolve.ImportWaitDeadline);
        Assert.Equal(cancellation.Token, sameResolve.CancellationToken);
        Assert.NotSame(resolve.ImportWaitDeadline, nextResolve.ImportWaitDeadline);
    }
}
