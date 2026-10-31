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
public class CrateDbStreamDataIntegralRoutingTests
{
    private static readonly OctoObjectId RawArchive = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId TimeRangeArchive = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId TwaRollup = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId AvgRollup = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> GridType = new("EnergyIQ", new CkTypeId("GridConnection"));
    private static readonly DateTime From = new(2026, 10, 30, 10, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 30, 11, 15, 0, DateTimeKind.Utc);

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();
    private readonly List<string> _executedSql = new();

    private static readonly IOptions<StreamDataConfiguration> Config =
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" });

    public CrateDbStreamDataIntegralRoutingTests()
    {
        A.CallTo(() => _store.GetAsync(RawArchive)).Returns(
            new ArchiveSnapshot(RawArchive, GridType, CkArchiveStatus.Activated, "raw",
                [
                    new CkArchiveColumnSpec("ActivePower", Indexed: true, Required: false),
                    new CkArchiveColumnSpec("Site", Indexed: false, Required: false)
                ]));
        A.CallTo(() => _store.GetAsync(TimeRangeArchive)).Returns(
            new ArchiveSnapshot(TimeRangeArchive, GridType, CkArchiveStatus.Activated, "timerange",
                [new CkArchiveColumnSpec("ActivePower", Indexed: false, Required: false)])
            {
                IsTimeRange = true
            });
        RegisterRollup(TwaRollup, new CkRollupAggregationSpec("ActivePower", CkRollupFunction.TimeWeightedAvg, null));
        RegisterRollup(AvgRollup, new CkRollupAggregationSpec("ActivePower", CkRollupFunction.Avg, null));
        A.CallTo(() => _db.StreamRawRowsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(AsyncEnumerable.Empty<IReadOnlyDictionary<string, object?>>());
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._))
            .Invokes((string _, string sql) => _executedSql.Add(sql))
            .Returns(Task.FromResult(new List<DataPointDto>()));
    }

    private readonly IRollupArchiveRuntimeStore _rollups = A.Fake<IRollupArchiveRuntimeStore>();

    /// <summary>A 5-minute rollup over <see cref="RawArchive"/> with the given aggregations.</summary>
    private void RegisterRollup(OctoObjectId rtId, params CkRollupAggregationSpec[] specs)
    {
        A.CallTo(() => _store.GetAsync(rtId)).Returns(
            new ArchiveSnapshot(rtId, GridType, CkArchiveStatus.Activated, null, RollupColumnGenerator.Generate(specs))
            {
                RollupAggregations = specs,
                Period = TimeSpan.FromMinutes(5)
            });
        A.CallTo(() => _rollups.GetAsync(rtId)).Returns(
            new RollupArchiveSnapshot(rtId, GridType, CkArchiveStatus.Activated, null,
                [new RollupSourceReference(RawArchive)], TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), null, specs, null));
    }

    private CrateDbStreamDataRepository NewSut() =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), _db, A.Fake<IStreamDataDatabaseManagementClient>(), Config, "tenant-x",
            _store, A.Fake<IFormulaEngine>(), _rollups);

    private static AggregationColumn IntegralOf(string path) => new(path, AggregationFunction.Integral);

    private void AssertNoSqlExecuted() =>
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._)).MustNotHaveHappened();

    [Fact]
    public async Task Aggregation_IntegralOnRawArchive_UsesTheLocfStatement()
    {
        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(GridType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithTimeRange(From, To);

        await NewSut().ExecuteAggregationQueryAsync(RawArchive, options);

        var sql = Assert.Single(_executedSql);
        Assert.Contains("* \"dt_ms\" END) / 3600000.0 AS \"activepower_integral\"", sql);
        Assert.Contains("TRUE AS \"is_carry\"", sql);
        Assert.DoesNotContain("GROUP BY", sql);
    }

    [Fact]
    public async Task GroupedAggregation_IntegralOnRawArchive_UsesTheLocfStatement_GroupedByTheColumn()
    {
        var options = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(GridType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithGroupByColumns(["Site"])
            .WithTimeRange(From, To);

        await NewSut().ExecuteGroupedAggregationQueryAsync(RawArchive, options);

        var sql = Assert.Single(_executedSql);
        Assert.Contains("AS \"activepower_integral\"", sql);
        Assert.Contains("GROUP BY \"site\"", sql);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Aggregation_IntegralWithoutACompleteWindow_IsAStructuredError(bool withFrom, bool withTo)
    {
        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(GridType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithTimeRange(withFrom ? From : null, withTo ? To : null);

        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteAggregationQueryAsync(RawArchive, options));

        Assert.Contains("require From and To", ex.Message, StringComparison.Ordinal);
        Assert.Equal(RawArchive, ex.ArchiveRtId);
        AssertNoSqlExecuted();
    }

    [Fact]
    public async Task GroupedAggregation_IntegralWithoutTo_IsAStructuredError()
    {
        var options = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(GridType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithGroupByColumns(["Site"])
            .WithTimeRange(From, null);

        await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteGroupedAggregationQueryAsync(RawArchive, options));
        AssertNoSqlExecuted();
    }

    [Fact]
    public async Task Aggregation_IntegralOnATimeRangeArchive_IsRefused_InsteadOfAWrongResult()
    {
        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(GridType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithTimeRange(From, To);

        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteAggregationQueryAsync(TimeRangeArchive, options));

        Assert.Contains("time-range archives are not supported", ex.Message, StringComparison.Ordinal);
        Assert.Equal(TimeRangeArchive, ex.ArchiveRtId);
        AssertNoSqlExecuted();
    }

    [Fact]
    public async Task GroupedAggregation_IntegralOnATimeRangeArchive_IsRefused()
    {
        var options = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(GridType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithGroupByColumns(["ActivePower"])
            .WithTimeRange(From, To);

        await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteGroupedAggregationQueryAsync(TimeRangeArchive, options));
        AssertNoSqlExecuted();
    }
    // ---- Rollups (AB#7920) ----

    [Fact]
    public async Task Aggregation_IntegralOnATwaRollup_ReadsTheMaterialisedIntegral()
    {
        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(GridType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithTimeRange(From, To);

        var result = await NewSut().ExecuteAggregationQueryAsync(TwaRollup, options);

        var sql = Assert.Single(_executedSql);
        Assert.Contains("SUM(\"activepower_twavg_integral\") / 3600000.0 AS \"activepower_integral\"", sql);
        Assert.DoesNotContain("is_carry", sql); // no LOCF statement: the rollup already holds the integral
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task GroupedAggregation_IntegralOnATwaRollup_ReadsTheMaterialisedIntegral()
    {
        var options = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(GridType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithGroupByColumns(["rtId"])
            .WithTimeRange(From, To);

        await NewSut().ExecuteGroupedAggregationQueryAsync(TwaRollup, options);

        var sql = Assert.Single(_executedSql);
        Assert.Contains("SUM(\"activepower_twavg_integral\") / 3600000.0", sql);
        Assert.Contains("GROUP BY", sql);
    }

    [Fact]
    public async Task Downsampling_IntegralOnATwaRollup_ReadsTheMaterialisedIntegralPerBin()
    {
        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(GridType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithTimeRange(From, To)
            .WithLimit(3);

        await NewSut().ExecuteDownsamplingQueryAsync(TwaRollup, options);

        Assert.Contains(_executedSql, sql => sql.Contains("activepower_twavg_integral", StringComparison.Ordinal)
                                             && sql.Contains("/ 3600000.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Aggregation_IntegralOnARollupWithoutTwa_IsRefused_NamingTheMissingAggregation()
    {
        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(GridType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithTimeRange(From, To);

        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteAggregationQueryAsync(AvgRollup, options));

        Assert.Contains("needs a TimeWeightedAvg aggregation of 'ActivePower'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(AvgRollup, ex.ArchiveRtId);
        AssertNoSqlExecuted();
    }

    [Fact]
    public async Task GroupedAggregation_IntegralOnARollupWithoutTwa_IsRefused()
    {
        var options = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(GridType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithGroupByColumns(["rtId"])
            .WithTimeRange(From, To);

        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteGroupedAggregationQueryAsync(AvgRollup, options));
        Assert.Contains("TimeWeightedAvg", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Downsampling_IntegralOnARollupWithoutTwa_IsRefused()
    {
        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(GridType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithTimeRange(From, To)
            .WithLimit(3);

        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteDownsamplingQueryAsync(AvgRollup, options));
        Assert.Contains("TimeWeightedAvg", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Downsampling_IntegralOnARawArchive_IsAStructuredError_NotInvalidSql()
    {
        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(GridType)
            .WithAggregationColumns([IntegralOf("ActivePower")])
            .WithTimeRange(From, To)
            .WithLimit(3);

        var ex = await Assert.ThrowsAsync<StreamDataException>(
            () => NewSut().ExecuteDownsamplingQueryAsync(RawArchive, options));
        Assert.Contains("per downsampling bin", ex.Message, StringComparison.Ordinal);
        Assert.Equal(RawArchive, ex.ArchiveRtId);
        AssertNoSqlExecuted();
    }
}
