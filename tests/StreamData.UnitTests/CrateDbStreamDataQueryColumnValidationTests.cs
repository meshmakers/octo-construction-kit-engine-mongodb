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

    private CrateDbStreamDataRepository NewSut() =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance,
            _cache, _db, _mgmt, Config, "tenant-x",
            _store, _formula);

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
