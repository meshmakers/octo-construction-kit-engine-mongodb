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
// The contracts type: the REST controller turns it into a 400, the GraphQL layer into a coded error.
using StreamDataException = Meshmakers.Octo.Runtime.Contracts.StreamData.StreamDataException;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#7918 at the repository boundary: <c>Integral</c> is routed to the query-time LOCF statement
/// on raw archives (single-row and grouped), refused on time-range archives, and refused without a
/// complete window — each refusal a structured <see cref="StreamDataException"/> before any SQL
/// runs. AB#7920: on a rollup it reads the materialised TimeWeightedAvg integral (single-row,
/// grouped, downsampling) and is refused, naming the missing aggregation, when the rollup has none. The numbers themselves are covered against a real CrateDB in
/// <c>StreamDataIntegralIntegrationTests</c>.
/// </summary>

/// <summary>
/// AB#8168 at the repository boundary: several StateDuration columns on one attribute get
/// distinct, deterministic output keys (a single one keeps <c>{col}_stateduration</c>), the rollup read
/// path picks the state each column names, and duplicate columns are refused with a readable
/// <see cref="StreamDataException"/> instead of a generic error.
/// </summary>
public class StreamDataStateDurationAliasTests
{
    private static readonly OctoObjectId RawArchive = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId TimeRangeArchive = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId StateRollup = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> DeviceType = new("EnergyIQ", new CkTypeId("ShadingDevice"));
    private static readonly DateTime From = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();
    private readonly IRollupArchiveRuntimeStore _rollups = A.Fake<IRollupArchiveRuntimeStore>();
    private readonly List<string> _executedSql = new();

    private static readonly IOptions<StreamDataConfiguration> Config =
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" });

    public StreamDataStateDurationAliasTests()
    {
        A.CallTo(() => _store.GetAsync(RawArchive)).Returns(
            new ArchiveSnapshot(RawArchive, DeviceType, CkArchiveStatus.Activated, "raw",
                [new CkArchiveColumnSpec("Position", Indexed: true, Required: false)]));
        A.CallTo(() => _store.GetAsync(TimeRangeArchive)).Returns(
            new ArchiveSnapshot(TimeRangeArchive, DeviceType, CkArchiveStatus.Activated, "timerange",
                [new CkArchiveColumnSpec("Position", Indexed: false, Required: false)])
            {
                IsTimeRange = true
            });
        CkRollupAggregationSpec[] specs =
        [
            new("Position", CkRollupFunction.StateDuration, "position_state0", "0"),
            new("Position", CkRollupFunction.StateDuration, "position_state100", "100"),
        ];
        A.CallTo(() => _store.GetAsync(StateRollup)).Returns(
            new ArchiveSnapshot(StateRollup, DeviceType, CkArchiveStatus.Activated, null, RollupColumnGenerator.Generate(specs))
            {
                RollupAggregations = specs,
                Period = TimeSpan.FromMinutes(5)
            });
        A.CallTo(() => _rollups.GetAsync(StateRollup)).Returns(
            new RollupArchiveSnapshot(StateRollup, DeviceType, CkArchiveStatus.Activated, null,
                [new RollupSourceReference(RawArchive)], TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), null, specs, null));
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._))
            .Invokes((string _, string sql) => _executedSql.Add(sql))
            .Returns(Task.FromResult(new List<DataPointDto>()));
    }

    private CrateDbStreamDataRepository NewSut() =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), _db, A.Fake<IStreamDataDatabaseManagementClient>(), Config, "tenant-x",
            _store, A.Fake<IFormulaEngine>(), _rollups);

    private static AggregationColumn State(string value) => new("Position", AggregationFunction.StateDuration, value);

    private StreamDataAggregationQueryOptions Aggregation(params AggregationColumn[] columns) =>
        StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(DeviceType).WithAggregationColumns(columns).WithTimeRange(From, To);

    // ---- naming rule ----

    [Theory]
    [InlineData("0", "stateduration_0")]
    [InlineData("100", "stateduration_100")]
    [InlineData("-1", "stateduration_neg1")]
    [InlineData("-1.5", "stateduration_neg1_5")]
    [InlineData("Low Power", "stateduration_low_power")]
    [InlineData(" true ", "stateduration_true")]
    [InlineData("***", "stateduration_x")]
    public void FunctionSuffix_SanitizesTheComparisonValue(string value, string expected) =>
        Assert.Equal(expected, StreamDataAggregationOutputNaming.FunctionSuffix(value));

    [Fact]
    public void SingleStateDuration_KeepsTheContractSuffix()
    {
        var cols = new[] { State("0"), new AggregationColumn("Position", AggregationFunction.Average) };
        Assert.Equal("stateduration", StreamDataAggregationOutputNaming.StateDurationFunctionSuffix(cols, cols[0]));
    }

    [Fact]
    public void StateDurationOnDifferentAttributes_EachKeepTheContractSuffix()
    {
        var cols = new[] { State("0"), new AggregationColumn("Other", AggregationFunction.StateDuration, "0") };
        Assert.All(cols, c => Assert.Equal("stateduration", StreamDataAggregationOutputNaming.StateDurationFunctionSuffix(cols, c)));
    }

    [Fact]
    public void SeveralStateDurations_AreToldApartByTheirValue_CaseInsensitiveOnThePath()
    {
        var cols = new[] { State("0"), new AggregationColumn("position", AggregationFunction.StateDuration, "100") };
        Assert.Equal("stateduration_0", StreamDataAggregationOutputNaming.StateDurationFunctionSuffix(cols, cols[0]));
        Assert.Equal("stateduration_100", StreamDataAggregationOutputNaming.StateDurationFunctionSuffix(cols, cols[1]));
    }

    // ---- raw archive ----

    [Fact]
    public async Task Raw_TwoStateColumns_GetDistinctAliases()
    {
        await NewSut().ExecuteAggregationQueryAsync(RawArchive, Aggregation(State("0"), State("100")));

        var sql = Assert.Single(_executedSql);
        Assert.Contains("AS \"position_stateduration_0\"", sql);
        Assert.Contains("AS \"position_stateduration_100\"", sql);
        Assert.DoesNotContain("AS \"position_stateduration\"", sql);
    }

    [Fact]
    public async Task Raw_OneStateColumn_KeepsTheContractAlias()
    {
        await NewSut().ExecuteAggregationQueryAsync(RawArchive, Aggregation(State("0")));

        var sql = Assert.Single(_executedSql);
        Assert.Contains("AS \"position_stateduration\"", sql);
        Assert.DoesNotContain("position_stateduration_", sql);
    }

    [Fact]
    public async Task Raw_SameStateTwice_IsAReadableValidationError()
    {
        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteAggregationQueryAsync(RawArchive, Aggregation(State("0"), State(" 0 "))));

        Assert.Contains("distinct comparison value", ex.Message, StringComparison.Ordinal);
        Assert.Equal(RawArchive, ex.ArchiveRtId);
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Raw_SameAggregationTwice_IsAReadableValidationError()
    {
        var avg = new AggregationColumn("Position", AggregationFunction.Average);
        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteAggregationQueryAsync(RawArchive, Aggregation(avg, avg)));

        Assert.Contains("requested more than once", ex.Message, StringComparison.Ordinal);
    }

    // ---- rollup ----

    [Fact]
    public async Task Rollup_TwoStateColumns_ReadTheirOwnMaterialisedColumn_WithDistinctAliases()
    {
        await NewSut().ExecuteAggregationQueryAsync(StateRollup, Aggregation(State("0"), State("100")));

        var sql = Assert.Single(_executedSql);
        Assert.Contains("SUM(\"position_state0\") AS \"position_stateduration_0\"", sql);
        Assert.Contains("SUM(\"position_state100\") AS \"position_stateduration_100\"", sql);
    }

    [Fact]
    public async Task Rollup_OneStateColumn_ReadsTheMatchingStateUnderTheContractAlias()
    {
        await NewSut().ExecuteAggregationQueryAsync(StateRollup, Aggregation(State("100")));

        var sql = Assert.Single(_executedSql);
        Assert.Contains("SUM(\"position_state100\") AS \"position_stateduration\"", sql);
        Assert.DoesNotContain("position_state0", sql);
    }

    [Fact]
    public async Task Rollup_StateTheRollupDoesNotMaterialise_IsRefused_NotAnotherStatesValue()
    {
        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteAggregationQueryAsync(StateRollup, Aggregation(State("50"))));

        Assert.Contains("'50'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not materialise", ex.Message, StringComparison.Ordinal);
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Rollup_Downsampling_TwoStateColumns_GetDistinctAliases()
    {
        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(DeviceType)
            .WithAggregationColumns([State("0"), State("100")])
            .WithTimeRange(From, To)
            .WithLimit(3);

        await NewSut().ExecuteDownsamplingQueryAsync(StateRollup, options);

        Assert.Contains(_executedSql, sql => sql.Contains("\"position_stateduration_0\"", StringComparison.Ordinal)
                                             && sql.Contains("\"position_stateduration_100\"", StringComparison.Ordinal));
    }
}
