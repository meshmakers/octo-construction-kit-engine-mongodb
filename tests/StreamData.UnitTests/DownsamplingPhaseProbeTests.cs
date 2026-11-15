using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#8992: the window-phase probe of a time-range downsampling query (AB#8962) exhausted the CrateDB pool of a large
/// tenant (prod-2 sbeg: ten parallel chart queries, each probe retried under a 30 s timeout). The probe is now
/// remembered per archive, window length and filter set, runs once outside the resilience pipeline, and the query waits
/// for it only briefly. Every repository below shares one process state, as the repositories of one process do.
/// </summary>
public class DownsamplingPhaseProbeTests
{
    private static readonly OctoObjectId TimeRange = new("aa0000000000000000008992");
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly OctoObjectId SeriesA = new("aa00000000000000000089aa");
    private static readonly OctoObjectId SeriesB = new("aa00000000000000000089bb");
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>A relative window ("last 2 days") that starts between two stored windows.</summary>
    private static readonly DateTime From = new(2026, 11, 13, 18, 37, 42, DateTimeKind.Utc);

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();
    private readonly RecomputeProcessState _process = new();
    private readonly List<string> _queries = new();
    private int _probes;

    public DownsamplingPhaseProbeTests()
    {
        A.CallTo(() => _store.GetAsync(TimeRange)).Returns(new ArchiveSnapshot(TimeRange, Type, CkArchiveStatus.Activated, "tr",
            [new CkArchiveColumnSpec("Amount", true, false)])
        {
            IsTimeRange = true,
            Period = Window
        });
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._))
            .Invokes((string _, string sql) => _queries.Add(sql))
            .Returns(Task.FromResult(new List<DataPointDto>()));
        ProbeAnswers(0, 0);
    }

    /// <summary>The stored windows sit at <paramref name="pmin"/>..<paramref name="pmax"/> ms past the quarter hour.</summary>
    private void ProbeAnswers(long? pmin, long? pmax) =>
        A.CallTo(() => _db.QueryRowsOnceAsync(A<string>._, A<string>._, A<TimeSpan>._, A<CancellationToken>._))
            .Invokes(() => Interlocked.Increment(ref _probes))
            .Returns(Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                [new Dictionary<string, object?> { ["pmin"] = pmin, ["pmax"] = pmax }]));

    // Queries with limit 48 put four windows into a bin (k = 4): an empty result there triggers no check of the remembered
    // phase (only k = 1 does, see the OneBinPerWindow tests), so they count only the probes of the cache.
    private CrateDbStreamDataRepository NewSut(TimeSpan? cacheDuration = null, TimeSpan? probeTimeout = null) =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance, A.Fake<ICkCacheService>(), _db,
            A.Fake<IStreamDataDatabaseManagementClient>(), Options.Create(new StreamDataConfiguration
            {
                ConnectionString = "Host=x",
                DownsamplingPhaseProbeCacheDuration = cacheDuration ?? TimeSpan.FromMinutes(5),
                DownsamplingPhaseProbeTimeout = probeTimeout ?? TimeSpan.FromSeconds(5),
            }),
            "probe", _store, A.Fake<IFormulaEngine>(), recomputeProcessState: _process);

    private static StreamDataDownsamplingQueryOptions Query(DateTime from, int limit = 192, OctoObjectId? series = null)
    {
        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(Type)
            .WithAggregationColumns([new AggregationColumn("Amount", AggregationFunction.Sum)])
            .WithTimeRange(from, from.AddDays(2))
            .WithLimit(limit);
        return series is { } s ? options.WithRtIds([s]) : options;
    }

    private string LastOrigin() => _queries.Last();

    [Fact]
    public async Task ARepeatedQuery_TakesThePhaseFromTheCache_EvenWhenTheRelativeWindowMoved()
    {
        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From, limit: 48));
        // the next refresh of the chart, a minute later: From moved, the filters did not
        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From.AddMinutes(1), limit: 48));

        Assert.Equal(1, _probes);
        Assert.Contains("'2026-11-13 18:30:00.000Z'::TIMESTAMP", LastOrigin());
    }

    [Fact]
    public async Task OtherFilters_AreProbedOnTheirOwn()
    {
        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From, limit: 48, series: SeriesA));
        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From, limit: 48, series: SeriesB));
        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From, limit: 48, series: SeriesA));

        Assert.Equal(2, _probes);
    }

    [Fact]
    public async Task TenParallelQueriesOfOneSeries_RunOneProbe()
    {
        var release = new TaskCompletionSource<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _db.QueryRowsOnceAsync(A<string>._, A<string>._, A<TimeSpan>._, A<CancellationToken>._))
            .Invokes(() => Interlocked.Increment(ref _probes))
            .Returns(release.Task);

        var queries = Enumerable.Range(0, 10)
            .Select(_ => NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From)))
            .ToList();
        await WaitUntilAsync(() => Volatile.Read(ref _probes) > 0);
        release.SetResult([new Dictionary<string, object?> { ["pmin"] = 0L, ["pmax"] = 0L }]);
        await Task.WhenAll(queries);

        Assert.Equal(1, _probes);
        Assert.All(_queries, q => Assert.Contains("'2026-11-13 18:30:00.000Z'::TIMESTAMP", q));
    }

    [Fact]
    public async Task TheProbe_RunsOnceWithItsBudget_NeverThroughTheRetriedGetData()
    {
        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From));

        A.CallTo(() => _db.QueryRowsOnceAsync("probe", A<string>.That.StartsWith("SELECT MIN("),
                CrateDbStreamDataRepository.DetachedPhaseProbeBudget, CancellationToken.None))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>.That.StartsWith("SELECT MIN("))).MustNotHaveHappened();
    }

    [Fact]
    public async Task AFailedProbe_IsNotRetried_TheQueryGoesOnUnsnapped_AndTheNextQueryAsksAgain()
    {
        A.CallTo(() => _db.QueryRowsOnceAsync(A<string>._, A<string>._, A<TimeSpan>._, A<CancellationToken>._))
            .Invokes(() => Interlocked.Increment(ref _probes))
            .ThrowsAsync(new InvalidOperationException("pool exhausted"));

        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From));
        Assert.Equal(1, _probes);
        Assert.Contains("'2026-11-13 18:37:42.000Z'::TIMESTAMP", LastOrigin());

        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From));
        Assert.Equal(2, _probes);
    }

    [Fact]
    public async Task AProbeThatDoesNotAnswerInTime_LetsTheQueryGoOnUnsnapped_AndFillsTheCacheInTheBackground()
    {
        var release = new TaskCompletionSource<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _db.QueryRowsOnceAsync(A<string>._, A<string>._, A<TimeSpan>._, A<CancellationToken>._))
            .Invokes(() => Interlocked.Increment(ref _probes))
            .Returns(release.Task);

        await NewSut(probeTimeout: TimeSpan.FromMilliseconds(100)).ExecuteDownsamplingQueryAsync(TimeRange, Query(From, limit: 48));
        Assert.Contains("'2026-11-13 18:37:42.000Z'::TIMESTAMP", LastOrigin());
        Assert.Equal(1, _process.WindowPhases.InFlightCount);

        // the detached probe answers after its query has gone
        release.SetResult([new Dictionary<string, object?> { ["pmin"] = 0L, ["pmax"] = 0L }]);
        await WaitUntilAsync(() => _process.WindowPhases.Count == 1);

        await NewSut(probeTimeout: TimeSpan.FromMilliseconds(100)).ExecuteDownsamplingQueryAsync(TimeRange, Query(From, limit: 48));
        Assert.Equal(1, _probes);
        Assert.Contains("'2026-11-13 18:30:00.000Z'::TIMESTAMP", LastOrigin());
    }

    [Fact]
    public async Task WithTheCacheOff_EveryQueryProbes_WithTheShortBudgetAndNothingOutlivesTheQuery()
    {
        await NewSut(cacheDuration: TimeSpan.Zero).ExecuteDownsamplingQueryAsync(TimeRange, Query(From));
        await NewSut(cacheDuration: TimeSpan.Zero).ExecuteDownsamplingQueryAsync(TimeRange, Query(From));

        Assert.Equal(2, _probes);
        A.CallTo(() => _db.QueryRowsOnceAsync(A<string>._, A<string>._, TimeSpan.FromSeconds(5), A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
        Assert.Equal(0, _process.WindowPhases.Count);
        Assert.Equal(0, _process.WindowPhases.InFlightCount);
        Assert.Contains("'2026-11-13 18:30:00.000Z'::TIMESTAMP", LastOrigin());
    }

    [Fact]
    public async Task OneBinPerWindow_AStaleRememberedPhase_IsEvicted_AndTheQueryRunsAgainWithTheFreshPhase()
    {
        // remembered: windows at :00/:15/:30/:45
        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From));
        // the archive now stores its windows at :05/:20/:35/:50; with the old phase every window straddles a bin and
        // the §7 predicate drops all of them: the query returns no row
        ProbeAnswers(300_000, 300_000);
        _queries.Clear();

        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From));

        Assert.Equal(2, _probes);
        Assert.Equal(2, _queries.Count);
        Assert.Contains("'2026-11-13 18:30:00.000Z'::TIMESTAMP", _queries[0]);
        Assert.Contains("'2026-11-13 18:35:00.000Z'::TIMESTAMP", _queries[1]);
        Assert.Equal(1, _process.WindowPhases.Count);
    }

    [Fact]
    public async Task OneBinPerWindow_AnEmptyResultWithAPhaseThatStillHolds_DoesNotRunTheQueryAgain()
    {
        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From));
        _queries.Clear();

        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From));

        // the cached phase was checked once (the result was empty), it still holds, the query is not repeated
        Assert.Equal(2, _probes);
        Assert.Single(_queries);
    }

    [Fact]
    public async Task SeveralWindowsPerBin_AnEmptyResult_IsNotTakenAsAStalePhase()
    {
        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From, limit: 48));
        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From, limit: 48));

        // 48 bins over two days = 1 h = 4 windows per bin: a stale phase drops only part of the windows, an empty result
        // says nothing about the phase
        Assert.Equal(1, _probes);
    }

    [Fact]
    public async Task AQueryWithData_DoesNotCheckTheRememberedPhase()
    {
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._))
            .Invokes((string _, string sql) => _queries.Add(sql))
            .Returns(Task.FromResult(new List<DataPointDto>
            {
                new(new Dictionary<string, object?> { ["T"] = new DateTime(2026, 11, 13, 18, 30, 0, DateTimeKind.Utc) })
                {
                    Timestamp = new DateTime(2026, 11, 13, 18, 30, 0, DateTimeKind.Utc)
                }
            }));

        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From));
        await NewSut().ExecuteDownsamplingQueryAsync(TimeRange, Query(From));

        Assert.Equal(1, _probes);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached within 10 s");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
