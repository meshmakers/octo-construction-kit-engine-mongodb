using System.Globalization;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.ComputedColumns;

/// <summary>
///     AB#8002 against a real CrateDB: adding a computed column queues its backfill and returns at once, the drain runs it
///     in time slices under the maintenance budget, the request's cancellation cannot reach it, two backfills queue and both
///     end Active with every row written, and a failed backfill is retried with one call.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class ComputedColumnBackfillJobIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> SensorType = new("Test", new CkTypeId("Sensor"));

    private static readonly IFormulaEngine Formula =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Lane(
        string Tenant, OctoObjectId Archive, string Table, MemoryArchiveStore Store, MemoryJobStore Jobs,
        ArchiveLifecycleService Lifecycle);

    private async Task<Lane> NewLaneAsync(int hourlyRows, IStreamDataDatabaseClient? client = null)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var archive = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, archive, hourlyRows);

        var store = new MemoryArchiveStore();
        store.Put(new ArchiveSnapshot(archive, SensorType, CkArchiveStatus.Activated, "sensor",
            new List<CkArchiveColumnSpec> { new("Value", Indexed: false, Required: false) }));
        var jobs = new MemoryJobStore();
        var configuration = Options.Create(new StreamDataConfiguration
        {
            ConnectionString = fixture.ConnectionString,
            // 1500 hourly rows span ~62 days: a one-day slice makes the backfill walk 63 slices.
            ComputedColumnBackfillSlice = TimeSpan.FromDays(1),
        });
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), client ?? fixture.Client, A.Fake<IStreamDataDatabaseManagementClient>(),
            configuration, tenant, store, Formula);
        var lifecycle = new ArchiveLifecycleService(tenant, store, repository, A.Fake<IArchiveAuditTrail>(),
            NullLogger<ArchiveLifecycleService>.Instance, recomputeJobStore: jobs);
        return new Lane(tenant, archive, TenantSchema.QualifiedArchiveTable(tenant, archive.ToString()), store, jobs, lifecycle);
    }

    private async Task<(long Rows, long Written, long Wrong)> CheckAsync(string table, string column, double factor)
    {
        await fixture.SqlAsync($"REFRESH TABLE {table}");
        var rows = await fixture.CountAsync($"SELECT count(*) FROM {table}");
        var written = await fixture.CountAsync($"SELECT count(*) FROM {table} WHERE \"{column}\" IS NOT NULL");
        var wrong = await fixture.CountAsync(
            $"SELECT count(*) FROM {table} WHERE abs(\"{column}\" - \"value\" * {factor.ToString(CultureInfo.InvariantCulture)}) > 0.000001");
        return (rows, written, wrong);
    }

    [Fact]
    public async Task Enqueue_ReturnsBackfilling_TheDrainWritesEveryRowInSlices_EvenAfterTheRequestWasCancelled()
    {
        var lane = await NewLaneAsync(hourlyRows: 1500);
        using var request = new CancellationTokenSource();

        var job = await lane.Lifecycle.EnqueueAddComputedColumnAsync(
            lane.Archive, "doubled", "value * 2", FormulaResultType.Double, indexed: false, request.Token);
        await request.CancelAsync(); // the client gives up right after the call returned

        Assert.Equal(RecomputeJobState.Pending, job!.State);
        Assert.Equal(ComputedColumnState.Backfilling, lane.Store.Column(lane.Archive, "doubled")!.ComputedState);
        Assert.Equal(0, (await CheckAsync(lane.Table, "doubled", 2)).Written);

        var completed = await lane.Lifecycle.RunPendingComputedColumnBackfillsAsync(Ct);

        Assert.Equal(1, completed);
        Assert.Equal(ComputedColumnState.Active, lane.Store.Column(lane.Archive, "doubled")!.ComputedState);
        var (rows, written, wrong) = await CheckAsync(lane.Table, "doubled", 2);
        Assert.Equal(1500, rows);
        Assert.Equal(1500, written);
        Assert.Equal(0, wrong);
        var done = await lane.Jobs.GetAsync(job.RtId);
        Assert.Equal(RecomputeJobState.Completed, done!.State);
        Assert.Equal(1500, done.RowsProcessed);
    }

    [Fact]
    public async Task ARolloutMidRun_HandsTheJobBack_AndTheNextRunResumesFromTheCheckpoint()
    {
        // The real client; a "rollout" (shutdown) is triggered after the 20th committed page.
        var client = A.Fake<IStreamDataDatabaseClient>(o => o.Wrapping(fixture.Client));
        var lane = await NewLaneAsync(hourlyRows: 1500, client);
        using var rollout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pages = 0;
        A.CallTo(() => client.ExecuteBulkAsync(A<string>._, A<string>._, A<IReadOnlyList<IReadOnlyList<object?>>>._,
                A<CrateStatementClass>._, A<CancellationToken>._))
            .Invokes(() =>
            {
                if (Interlocked.Increment(ref pages) == 20)
                {
                    rollout.Cancel();
                }
            })
            .CallsWrappedMethod();

        var job = await lane.Lifecycle.EnqueueAddComputedColumnAsync(
            lane.Archive, "doubled", "value * 2", FormulaResultType.Double, indexed: false, Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lane.Lifecycle.RunPendingComputedColumnBackfillsAsync(rollout.Token));

        var handedBack = await lane.Jobs.GetAsync(job!.RtId);
        Assert.Equal(RecomputeJobState.Pending, handedBack!.State);
        Assert.True(handedBack.RangeStart > DateTime.UnixEpoch, "the hand-back keeps the checkpoint");
        var rowsBefore = handedBack.RowsProcessed ?? 0;
        Assert.InRange(rowsBefore, 1, 1499);
        var pagesBefore = pages;

        // "New pod": resumes at the checkpoint and finishes the column.
        Assert.Equal(1, await lane.Lifecycle.RunPendingComputedColumnBackfillsAsync(Ct));

        var done = await lane.Jobs.GetAsync(job.RtId);
        Assert.Equal(RecomputeJobState.Completed, done!.State);
        Assert.Equal(ComputedColumnState.Active, lane.Store.Column(lane.Archive, "doubled")!.ComputedState);
        var (rows, written, wrong) = await CheckAsync(lane.Table, "doubled", 2);
        Assert.Equal(1500, rows);
        Assert.Equal(1500, written);
        Assert.Equal(0, wrong);
        // The second run did not start over: it wrote only what was left (one-day slices of 24 rows).
        Assert.True(pages - pagesBefore < 63 - 15, $"resumed run wrote {pages - pagesBefore} pages");
        Assert.Equal(1500, done.RowsProcessed);
    }

    [Fact]
    public async Task TwoBackfills_Queue_AndBothEndActive()
    {
        var lane = await NewLaneAsync(hourlyRows: 1500);

        var first = await lane.Lifecycle.EnqueueAddComputedColumnAsync(
            lane.Archive, "doubled", "value * 2", FormulaResultType.Double, indexed: false, Ct);
        var second = await lane.Lifecycle.EnqueueAddComputedColumnAsync(
            lane.Archive, "negated", "value * -1", FormulaResultType.Double, indexed: false, Ct);

        Assert.Equal(2, await lane.Lifecycle.RunPendingComputedColumnBackfillsAsync(Ct));

        Assert.Equal(0, (await CheckAsync(lane.Table, "doubled", 2)).Wrong);
        Assert.Equal(0, (await CheckAsync(lane.Table, "negated", -1)).Wrong);
        Assert.Equal(1500, (await CheckAsync(lane.Table, "negated", -1)).Written);
        Assert.Equal(RecomputeJobState.Completed, (await lane.Jobs.GetAsync(first!.RtId))!.State);
        Assert.Equal(RecomputeJobState.Completed, (await lane.Jobs.GetAsync(second!.RtId))!.State);
        // The second only started after the first had finished.
        Assert.True((await lane.Jobs.GetAsync(second.RtId))!.StartedAt >= (await lane.Jobs.GetAsync(first.RtId))!.FinishedAt);
    }

    [Fact]
    public async Task FailedBackfill_EndsFailedWithReason_AndOneRetryCompletesIt()
    {
        // The real client, except that the first bulk write fails like a job CrateDB killed.
        var failing = A.Fake<IStreamDataDatabaseClient>(o => o.Wrapping(fixture.Client));
        A.CallTo(() => failing.ExecuteBulkAsync(A<string>._, A<string>._, A<IReadOnlyList<IReadOnlyList<object?>>>._,
                A<CrateStatementClass>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("XX000: Job killed. Cancellation request by: octo-system"))
            .Once();
        var lane = await NewLaneAsync(hourlyRows: 300, failing);

        var job = await lane.Lifecycle.EnqueueAddComputedColumnAsync(
            lane.Archive, "doubled", "value * 2", FormulaResultType.Double, indexed: false, Ct);
        await lane.Lifecycle.RunPendingComputedColumnBackfillsAsync(Ct);

        Assert.Equal(ComputedColumnState.Failed, lane.Store.Column(lane.Archive, "doubled")!.ComputedState);
        var failed = await lane.Jobs.GetAsync(job!.RtId);
        Assert.Equal(RecomputeJobState.Failed, failed!.State);
        Assert.Contains("Job killed", failed.ErrorReason);

        var retry = await lane.Lifecycle.RetryComputedColumnBackfillAsync(lane.Archive, "doubled", Ct);
        Assert.Equal(1, await lane.Lifecycle.RunPendingComputedColumnBackfillsAsync(Ct));

        Assert.Equal(ComputedColumnState.Active, lane.Store.Column(lane.Archive, "doubled")!.ComputedState);
        Assert.Equal(RecomputeJobState.Completed, (await lane.Jobs.GetAsync(retry!.RtId))!.State);
        var (_, written, wrong) = await CheckAsync(lane.Table, "doubled", 2);
        Assert.Equal(300, written);
        Assert.Equal(0, wrong);
    }
}
