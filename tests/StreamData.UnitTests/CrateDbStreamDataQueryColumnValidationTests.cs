using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
// The contracts type, not the connection-level one local to the CrateDb namespace: this is what
// StreamDataController catches and turns into a 400 carrying the message.
using StreamDataException = Meshmakers.Octo.Runtime.Contracts.StreamData.StreamDataException;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#4765 at the repository boundary: every query kind must reject an unresolvable column name
/// <em>before</em> it reaches the database. The pure validator tests cover which names are refused;
/// these cover that each entry point actually asks, and that the refusal happens early enough that no
/// SQL is executed — the wrong-result-shaped failure this bug produced was worse than an error.
/// </summary>
public class CrateDbStreamDataQueryColumnValidationTests
{
    private const string BadName = "WindowStart";  // the physical column is window_start
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> SomeType = new("Test", new CkTypeId("TempSensor"));

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IStreamDataDatabaseManagementClient _mgmt = A.Fake<IStreamDataDatabaseManagementClient>();
    private readonly ICkCacheService _cache = A.Fake<ICkCacheService>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();
    private readonly IFormulaEngine _formula = A.Fake<IFormulaEngine>();

    private static readonly IOptions<StreamDataConfiguration> Config =
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" });

    public CrateDbStreamDataQueryColumnValidationTests()
    {
        // A time-range archive with one ingested column, so window_start/window_end are the defaults
        // and "Temperature" is the one valid data column.
        A.CallTo(() => _store.GetAsync(Archive)).Returns(
            new ArchiveSnapshot(Archive, SomeType, CkArchiveStatus.Activated, "archive",
                [new CkArchiveColumnSpec("Temperature", Indexed: false, Required: false)])
            {
                IsTimeRange = true
            });
    }

    private readonly IRollupArchiveRuntimeStore _rollupStore = A.Fake<IRollupArchiveRuntimeStore>();

    private CrateDbStreamDataRepository NewSut() =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance,
            _cache, _db, _mgmt, Config, "tenant-x",
            _store, _formula, _rollupStore);

    /// <summary>The query must not reach CrateDB — that is the whole point of validating up front.</summary>
    private void AssertNoSqlExecuted() =>
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._)).MustNotHaveHappened();

    [Fact]
    public async Task SimpleQuery_UnknownProjectedColumn_Throws()
    {
        var options = StreamDataQueryOptions.Create()
            .WithCkTypeId(SomeType)
            .WithColumns([BadName]);

        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteQueryAsync(Archive, options));

        Assert.Contains(BadName, ex.Message);
        AssertNoSqlExecuted();
    }

    [Fact]
    public async Task SimpleQuery_UnknownSortColumn_Throws()
    {
        // The reported symptom: rows came back in storage order and the sort looked broken.
        var options = StreamDataQueryOptions.Create()
            .WithCkTypeId(SomeType)
            .WithColumns(["Temperature"])
            .WithSortOrders([new SortOrderItem(BadName, SortOrders.Ascending)]);

        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteQueryAsync(Archive, options));

        Assert.Contains("sorting", ex.Message);
        AssertNoSqlExecuted();
    }

    [Fact]
    public async Task SimpleQuery_UnknownFilterColumn_Throws()
    {
        // The dangerous one: the filter was dropped and the query became a full read.
        var options = StreamDataQueryOptions.Create()
            .WithCkTypeId(SomeType)
            .WithColumns(["Temperature"])
            .WithFieldFilters([new FieldFilter("Temparatur", FieldFilterOperator.Equals, "21")]);

        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteQueryAsync(Archive, options));

        Assert.Contains("field filter", ex.Message);
        AssertNoSqlExecuted();
    }

    [Fact]
    public async Task AggregationQuery_UnknownColumn_Throws()
    {
        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(SomeType)
            .WithAggregationColumns([new AggregationColumn("Temparatur", AggregationFunction.Sum)]);

        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteAggregationQueryAsync(Archive, options));

        Assert.Contains("aggregation", ex.Message);
        AssertNoSqlExecuted();
    }

    [Fact]
    public async Task GroupedAggregationQuery_UnknownGroupByColumn_Throws()
    {
        // Losing a group-by column merges groups: fewer rows, larger numbers, no error.
        var options = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(SomeType)
            .WithAggregationColumns([new AggregationColumn("Temperature", AggregationFunction.Sum)])
            .WithGroupByColumns(["rtld"]);  // lowercase L, not rtId

        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteGroupedAggregationQueryAsync(Archive, options));

        Assert.Contains("grouping", ex.Message);
        AssertNoSqlExecuted();
    }

    [Fact]
    public async Task DownsamplingQuery_UnknownColumn_Throws()
    {
        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(SomeType)
            .WithAggregationColumns([new AggregationColumn("Temparatur", AggregationFunction.Average)])
            .WithTimeRange(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc))
            .WithLimit(24);

        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteDownsamplingQueryAsync(Archive, options));

        Assert.Contains("aggregation", ex.Message);
        AssertNoSqlExecuted();
    }

    [Fact]
    public async Task DownsamplingQuery_GroupByRtId_IsAccepted()
    {
        // Guards the removal of the guessing fallback: rtId is a default field and must keep working
        // as a per-series group-by (AB#4233). Its old justification was that rtId does not resolve —
        // it does, and the fallback only ever masked typos.
        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(SomeType)
            .WithAggregationColumns([new AggregationColumn("Temperature", AggregationFunction.Average)])
            .WithGroupByColumns(["rtId"])
            .WithTimeRange(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc))
            .WithLimit(24);

        await NewSut().ExecuteDownsamplingQueryAsync(Archive, options);

        // Got past validation and issued its query.
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._)).MustHaveHappened();
    }

    [Fact]
    public async Task RollupAggregation_OnALogicalChainPath_IsAccepted()
    {
        // Regression: AB#4765 validated aggregation columns against the archive's own columns, but on
        // a rollup an aggregation column may name a LOGICAL source path that is deliberately not a
        // column of the rollup table. A TWA rollup stores voltage_twavg_integral /
        // voltage_twavg_duration; the query asks to aggregate "Voltage" and the chain resolver rewrites
        // that into an expression over the stored pair — a branch that runs before the field resolver
        // is ever consulted. Rejecting it broke the cascade-TWA integration tests in
        // octo-asset-repo-services and every GetStreamData fixture in the mesh adapter.
        var rollupRt = OctoObjectId.GenerateNewId();
        var sourceRt = OctoObjectId.GenerateNewId();

        A.CallTo(() => _store.GetAsync(rollupRt)).Returns(
            new ArchiveSnapshot(rollupRt, SomeType, CkArchiveStatus.Activated, "rollup",
                [
                    new CkArchiveColumnSpec("voltage_twavg_integral", Indexed: false, Required: false),
                    new CkArchiveColumnSpec("voltage_twavg_duration", Indexed: false, Required: false)
                ])
            {
                IsTimeRange = true,
                RollupAggregations = [new CkRollupAggregationSpec("Voltage", CkRollupFunction.TimeWeightedAvg, null)]
            });

        A.CallTo(() => _rollupStore.GetAsync(rollupRt)).Returns(
            new RollupArchiveSnapshot(rollupRt, SomeType, CkArchiveStatus.Activated, null, sourceRt,
                TimeSpan.FromHours(1), TimeSpan.Zero, null,
                [new CkRollupAggregationSpec("Voltage", CkRollupFunction.TimeWeightedAvg, null)],
                null));

        // The chain terminates at a raw archive, where the spec's source path is already logical.
        A.CallTo(() => _store.GetAsync(sourceRt)).Returns(
            new ArchiveSnapshot(sourceRt, SomeType, CkArchiveStatus.Activated, "source",
                [new CkArchiveColumnSpec("Voltage", Indexed: false, Required: false)]));
        A.CallTo(() => _rollupStore.GetAsync(sourceRt))
            .Returns(Task.FromResult<RollupArchiveSnapshot?>(null));

        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(SomeType)
            .WithAggregationColumns([new AggregationColumn("Voltage", AggregationFunction.TimeWeightedAverage)])
            .WithTimeRange(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc));

        // Must get past validation. Whether the chain resolver then finds a pair is a different
        // concern — the point is that validation no longer refuses the query outright.
        await NewSut().ExecuteAggregationQueryAsync(rollupRt, options);
    }

    [Fact]
    public async Task RollupValidation_ReadsEachChainSnapshotOnce_RegardlessOfSpecCount()
    {
        // The chain walk descends once per aggregation spec, and every spec of one rollup walks the
        // same chain. Unmemoised, a rollup with several aggregations over a cascade multiplies store
        // reads on every aggregating query — where validation previously did none at all.
        var rollupRt = OctoObjectId.GenerateNewId();
        var sourceRt = OctoObjectId.GenerateNewId();

        CkRollupAggregationSpec[] specs =
        [
            new("Voltage", CkRollupFunction.Sum, null),
            new("Voltage", CkRollupFunction.Max, null),
            new("Current", CkRollupFunction.Sum, null),
            new("Current", CkRollupFunction.Avg, null)
        ];

        A.CallTo(() => _store.GetAsync(rollupRt)).Returns(
            new ArchiveSnapshot(rollupRt, SomeType, CkArchiveStatus.Activated, "rollup",
                [new CkArchiveColumnSpec("voltage_sum", Indexed: false, Required: false)])
            {
                IsTimeRange = true,
                RollupAggregations = specs
            });
        A.CallTo(() => _rollupStore.GetAsync(rollupRt)).Returns(
            new RollupArchiveSnapshot(rollupRt, SomeType, CkArchiveStatus.Activated, null, sourceRt,
                TimeSpan.FromHours(1), TimeSpan.Zero, null, specs, null));
        A.CallTo(() => _store.GetAsync(sourceRt)).Returns(
            new ArchiveSnapshot(sourceRt, SomeType, CkArchiveStatus.Activated, "source",
                [
                    new CkArchiveColumnSpec("Voltage", Indexed: false, Required: false),
                    new CkArchiveColumnSpec("Current", Indexed: false, Required: false)
                ]));
        A.CallTo(() => _rollupStore.GetAsync(sourceRt))
            .Returns(Task.FromResult<RollupArchiveSnapshot?>(null));

        await NewSut().ExecuteAggregationQueryAsync(rollupRt,
            StreamDataAggregationQueryOptions.Create()
                .WithCkTypeId(SomeType)
                .WithAggregationColumns([new AggregationColumn("Voltage", AggregationFunction.Sum)]));

        // The reads must not scale with the spec count. Memoised, validation reads the source once
        // (2 total here — the query path's own ResolveRollupChainAggregationAsync walks the chain with
        // its own uncached loaders, which this fix does not touch). Unmemoised, validation alone would
        // read it once per spec, so 4 + 1 = 5. The bound below separates the two without pinning the
        // other path's behaviour.
        A.CallTo(() => _store.GetAsync(sourceRt))
            .MustHaveHappened(specs.Length - 1, Times.OrLess);
        A.CallTo(() => _rollupStore.GetAsync(sourceRt))
            .MustHaveHappened(specs.Length - 1, Times.OrLess);
    }

    [Fact]
    public async Task RollupAggregation_OnAnUnknownPath_IsStillRejected()
    {
        // The widening must not become a blanket exemption for rollups: a name that is neither a
        // column nor a resolvable chain path stays refused.
        var rollupRt = OctoObjectId.GenerateNewId();

        A.CallTo(() => _store.GetAsync(rollupRt)).Returns(
            new ArchiveSnapshot(rollupRt, SomeType, CkArchiveStatus.Activated, "rollup",
                [new CkArchiveColumnSpec("voltage_twavg_integral", Indexed: false, Required: false)])
            {
                IsTimeRange = true,
                RollupAggregations = [new CkRollupAggregationSpec("Voltage", CkRollupFunction.TimeWeightedAvg, null)]
            });
        A.CallTo(() => _rollupStore.GetAsync(rollupRt))
            .Returns(Task.FromResult<RollupArchiveSnapshot?>(null));

        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(SomeType)
            .WithAggregationColumns([new AggregationColumn("Voltaage", AggregationFunction.Sum)]);

        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteAggregationQueryAsync(rollupRt, options));

        Assert.Contains("Voltaage", ex.Message);
        AssertNoSqlExecuted();
    }

    [Fact]
    public async Task SimpleQuery_ValidNames_ReachTheDatabase()
    {
        var options = StreamDataQueryOptions.Create()
            .WithCkTypeId(SomeType)
            .WithColumns(["Temperature"])
            .WithSortOrders([new SortOrderItem("window_start", SortOrders.Descending)])
            .WithFieldFilters([new FieldFilter("rtWellKnownName", FieldFilterOperator.Equals, "Sensor001")]);

        await NewSut().ExecuteQueryAsync(Archive, options);

        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._)).MustHaveHappened();
    }
}
