using System.Diagnostics;
using System.Globalization;
using System.Text;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataDownsampling;

/// <summary>
///     AB#8992 against a real CrateDB: the window-phase probe of a time-range downsampling query (AB#8962) must not exhaust
///     the tenant's connection pool. prod-2 tenant sbeg, 2026-11-15: a MeshBoard line chart sent ~10 parallel
///     downsampling queries (one per OBIS group) on a time-range archive without rollup; every query probed first, under the
///     retried 30 s budget, and the pool of 32 ran dry.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class DownsamplingPhaseProbePoolIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> MeterType = new("Test", new CkTypeId("Meter"));
    private const string MeterTypeLiteral = "Test/Meter";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Two days of 15-minute windows stored at :05/:20/:35/:50, from this instant on.</summary>
    private static readonly DateTime FirstWindow = new(2026, 11, 12, 0, 5, 0, DateTimeKind.Utc);

    /// <summary>"Last 2 days" as a chart asks for it: between two stored windows.</summary>
    private static readonly DateTime From = new(2026, 11, 12, 18, 37, 42, DateTimeKind.Utc);

    private const int Series = 10;

    [Fact]
    public async Task TenParallelQueries_OnATimeRangeArchiveWithoutRollup_DoNotExhaustAPoolOf32_AndReadEveryWindow()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rtId = OctoObjectId.GenerateNewId();
        var series = await CreateArchiveAsync(tenant, rtId);
        await using var access = NewAccess(maxPoolSize: 32);
        var client = A.Fake<IStreamDataDatabaseClient>(o => o.Wrapping(NewClient(access)));
        var process = new RecomputeProcessState();

        // one query per series, like the line chart's OBIS groups, all at once
        var started = Stopwatch.StartNew();
        var results = await Task.WhenAll(series.Select(s =>
            NewRepository(tenant, rtId, client, process).ExecuteDownsamplingQueryAsync(rtId, Query(s))));
        started.Stop();

        // the axis snapped to :35, so every window inside [18:35, To) is read: none dropped by the §7 predicate
        var expected = await ExpectedWindowsPerSeriesAsync();
        Assert.All(results, r => Assert.Equal(expected, SumOf(r)));
        A.CallTo(() => client.QueryRowsOnceAsync(A<string>._, A<string>._, A<TimeSpan>._, A<CancellationToken>._))
            .MustHaveHappened(Series, Times.Exactly);
        A.CallTo(() => client.GetDataAsync(A<string>._, A<string>.That.StartsWith("SELECT MIN("))).MustNotHaveHappened();
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"took {started.Elapsed}");

        // the next refresh of the board, three times at once: the phases come from the cache, no further probe
        var refresh = await Task.WhenAll(Enumerable.Range(0, 3).SelectMany(_ => series).Select(s =>
            NewRepository(tenant, rtId, client, process).ExecuteDownsamplingQueryAsync(rtId, Query(s))));
        Assert.All(refresh, r => Assert.Equal(expected, SumOf(r)));
        A.CallTo(() => client.QueryRowsOnceAsync(A<string>._, A<string>._, A<TimeSpan>._, A<CancellationToken>._))
            .MustHaveHappened(Series, Times.Exactly);
    }

    [Fact]
    public async Task TheProbe_WithAFullPool_GivesUpWithinItsBudget_WithoutAPoolExhaustedError_AndLeavesThePoolUsable()
    {
        await using var access = NewAccess(maxPoolSize: 1);
        var client = NewClient(access);
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var held = await access.CreateConnectionAsync(tenant, Ct);

        var started = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.QueryRowsOnceAsync(tenant, "SELECT 1 AS \"x\"", TimeSpan.FromSeconds(1), Ct));
        started.Stop();

        // one budget for the wait, no retry: well below the pool's own 3 s wait, let alone the retried 35 s call
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2.5), $"took {started.Elapsed}");

        await held.DisposeAsync();
        var rows = await client.QueryRowsOnceAsync(tenant, "SELECT 1 AS \"x\"", TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(1L, Convert.ToInt64(Assert.Single(rows)["x"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task TheCancelledProbe_GivesItsConnectionBack()
    {
        await using var access = NewAccess(maxPoolSize: 1);
        var client = NewClient(access);
        var tenant = CrateStagingCopyFixture.NewTenantId();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.QueryRowsOnceAsync(tenant, "SELECT 1 AS \"x\"", TimeSpan.FromSeconds(5), cancelled.Token));

        // the only slot is free again
        var rows = await client.QueryRowsOnceAsync(tenant, "SELECT 2 AS \"x\"", TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(2L, Convert.ToInt64(Assert.Single(rows)["x"], CultureInfo.InvariantCulture));
    }

    /// <summary>A statement that keeps CrateDB busy for far longer than any budget below.</summary>
    private const string LongStatement = "SELECT COUNT(*) AS \"x\" FROM generate_series(1, 2000000000)";

    [Fact]
    public async Task AProbeCancelledWhileItsStatementRuns_EndsTheStatement_AndGivesItsConnectionBack()
    {
        // review of PR #247: the cancellation must reach a statement that holds the only pool slot, not only the wait
        await using var access = NewAccess(maxPoolSize: 1);
        var client = NewClient(access);
        var tenant = CrateStagingCopyFixture.NewTenantId();
        using var cancel = new CancellationTokenSource();

        var started = Stopwatch.StartNew();
        var running = client.QueryRowsOnceAsync(tenant, LongStatement, TimeSpan.FromSeconds(30), cancel.Token);
        // the statement holds the only slot: a second probe cannot get a connection within its budget
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.QueryRowsOnceAsync(tenant, "SELECT 1 AS \"x\"", TimeSpan.FromMilliseconds(500), Ct));
        Assert.False(running.IsCompleted);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => running);
        started.Stop();
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.Elapsed}");

        var rows = await client.QueryRowsOnceAsync(tenant, "SELECT 3 AS \"x\"", TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(3L, Convert.ToInt64(Assert.Single(rows)["x"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task AProbeWhoseBudgetRunsOutWhileItsStatementRuns_EndsTheStatement_AndGivesItsConnectionBack()
    {
        await using var access = NewAccess(maxPoolSize: 1);
        var client = NewClient(access);
        var tenant = CrateStagingCopyFixture.NewTenantId();

        var started = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(
            () => client.QueryRowsOnceAsync(tenant, LongStatement, TimeSpan.FromSeconds(1), Ct));
        started.Stop();
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"took {started.Elapsed}");

        var rows = await client.QueryRowsOnceAsync(tenant, "SELECT 4 AS \"x\"", TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(4L, Convert.ToInt64(Assert.Single(rows)["x"], CultureInfo.InvariantCulture));
    }

    private async Task<List<OctoObjectId>> CreateArchiveAsync(string tenant, OctoObjectId rtId)
    {
        var table = TenantSchema.QualifiedArchiveTable(tenant, rtId.ToString());
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateWindowedTable(table,
            [new ArchiveColumnDdl("Amount", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, true)], 2, 0));
        var series = Enumerable.Range(0, Series).Select(_ => OctoObjectId.GenerateNewId()).ToList();
        var starts = Enumerable.Range(0, 3 * 96).Select(i => FirstWindow + i * Window).ToList();
        foreach (var s in series)
        {
            var sql = new StringBuilder(
                $"INSERT INTO {table} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"amount\") VALUES ");
            sql.AppendJoin(", ", starts.Select(w => $"('{Iso(w)}', '{Iso(w + Window)}', '{s}', '{MeterTypeLiteral}', 1.0)"));
            await fixture.SqlAsync(sql.ToString());
        }

        await fixture.SqlAsync($"REFRESH TABLE {table}");
        return series;
    }

    /// <summary>
    ///     Windows of one series the snapped query reads: start in [18:35 on the first day, To). The source filter reads
    ///     windows that overlap the range, and every one of them lies wholly in a bin of the axis that starts at 18:35.
    /// </summary>
    private static Task<long> ExpectedWindowsPerSeriesAsync()
    {
        var origin = new DateTime(2026, 11, 12, 18, 35, 0, DateTimeKind.Utc);
        var to = From.AddDays(2);
        var count = Enumerable.Range(0, 3 * 96)
            .Select(i => FirstWindow + i * Window)
            .Count(w => w >= origin && w < to);
        return Task.FromResult((long)count);
    }

    private static long SumOf(StreamDataQueryResult result) => result.Rows
        .Select(r => r.Values["amount_sum"])
        .Where(v => v is not null)
        .Sum(v => Convert.ToInt64(v, CultureInfo.InvariantCulture));

    private static StreamDataDownsamplingQueryOptions Query(OctoObjectId series) =>
        StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(MeterType)
            .WithAggregationColumns([new AggregationColumn("Amount", AggregationFunction.Sum)])
            .WithTimeRange(From, From.AddDays(2))
            .WithLimit(48)
            .WithRtIds([series]);

    private CrateDbStreamDataRepository NewRepository(string tenant, OctoObjectId rtId, IStreamDataDatabaseClient client,
        RecomputeProcessState process)
    {
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(rtId)).Returns(new ArchiveSnapshot(rtId, MeterType,
            CkArchiveStatus.Activated, null, [new CkArchiveColumnSpec("Amount", true, false)])
        {
            IsTimeRange = true,
            Period = Window
        });
        return new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), client, fixture.Client, fixture.Configuration, tenant, store,
            A.Fake<IFormulaEngine>(), recomputeProcessState: process);
    }

    private CrateDbConnectionAccess NewAccess(int maxPoolSize)
    {
        // A short wait for a pool slot, so an exhausted pool fails the test in seconds.
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Timeout = 3 }.ConnectionString;
        return new CrateDbConnectionAccess(Options.Create(new StreamDataConfiguration
        {
            ConnectionString = connectionString,
            PoolingEnabled = true,
            MaxPoolSize = maxPoolSize,
        }), NullLogger<CrateDbConnectionAccess>.Instance);
    }

    private static CrateDatabaseClient NewClient(CrateDbConnectionAccess access) =>
        new(NullLogger<CrateDatabaseClient>.Instance, access,
            Options.Create(new StreamDataConfiguration { ConnectionString = "unused" }),
            Options.Create(new CrateResilienceOptions()),
            Options.Create(new StreamDataInstanceConfiguration()),
            Options.Create(new CrateRecomputeOptions()));

    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
