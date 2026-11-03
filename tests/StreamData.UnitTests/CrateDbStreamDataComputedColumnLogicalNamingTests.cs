using System.Text.RegularExpressions;
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
/// AB#8236: a computed column that went through a formula change lives in the versioned physical
/// column <c>{base}__v{N}</c>. The physical name is SQL-internal: every result alias, row key and
/// wire mapping key keeps the logical <c>{base}</c> name, whatever the version — a second formula
/// change must not rename the result again. Version 0 (never changed) is the baseline: names are
/// exactly what they were before.
/// </summary>
public class CrateDbStreamDataComputedColumnLogicalNamingTests
{
    private static readonly OctoObjectId RawArchive = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId TimeRangeArchive = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> BatteryType = new("EnergyIQ", new CkTypeId("BatteryStorage"));
    private static readonly DateTime From = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    // An alias that carries the version suffix: `AS "dischargepower__v1_integral"` etc.
    private static readonly Regex VersionedAlias = new("AS\\s+\"[^\"]*__v\\d", RegexOptions.Compiled);

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();
    private readonly List<string> _executedSql = new();

    private static readonly IOptions<StreamDataConfiguration> Config =
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" });

    public CrateDbStreamDataComputedColumnLogicalNamingTests()
    {
        A.CallTo(() => _db.StreamRawRowsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(AsyncEnumerable.Empty<IReadOnlyDictionary<string, object?>>());
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._))
            .Invokes((string _, string sql) => _executedSql.Add(sql))
            .Returns(Task.FromResult(new List<DataPointDto>()));
    }

    private static List<CkArchiveColumnSpec> Columns(int version) =>
    [
        new CkArchiveColumnSpec("ChargingPower", Indexed: false, Required: false),
        new CkArchiveColumnSpec(string.Empty, Indexed: false, Required: false)
        {
            Name = "DischargePower",
            Formula = "if(coalesce(chargingpower, 0) < 0, -chargingpower, 0)",
            ResultType = FormulaResultType.Double,
            ComputedState = ComputedColumnState.Active,
            ComputedVersion = version,
        }
    ];

    private static string Physical(int version) => version == 0 ? "dischargepower" : $"dischargepower__v{version}";

    private void Register(int version)
    {
        A.CallTo(() => _store.GetAsync(RawArchive)).Returns(
            new ArchiveSnapshot(RawArchive, BatteryType, CkArchiveStatus.Activated, "raw", Columns(version)));
        A.CallTo(() => _store.GetAsync(TimeRangeArchive)).Returns(
            new ArchiveSnapshot(TimeRangeArchive, BatteryType, CkArchiveStatus.Activated, "tr", Columns(version))
            { IsTimeRange = true, Period = TimeSpan.FromMinutes(15) });
    }

    private CrateDbStreamDataRepository NewSut() =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), _db, A.Fake<IStreamDataDatabaseManagementClient>(), Config, "tenant-x",
            _store, A.Fake<IFormulaEngine>(), A.Fake<IRollupArchiveRuntimeStore>());

    private string OnlySql()
    {
        var sql = Assert.Single(_executedSql);
        Assert.DoesNotMatch(VersionedAlias, sql);
        return sql;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SimpleQuery_ProjectsSortsAndFiltersOnThePhysicalColumn_ButAliasesTheLogicalName(int version)
    {
        Register(version);
        var options = StreamDataQueryOptions.Create()
            .WithCkTypeId(BatteryType)
            .WithColumns(["DischargePower"])
            .WithSortOrders([new SortOrderItem("DischargePower", SortOrders.Descending)])
            .WithFieldFilters([new FieldFilter("DischargePower", FieldFilterOperator.GreaterThan, "0")])
            .WithTimeRange(From, To);

        await NewSut().ExecuteQueryAsync(RawArchive, options);

        var sql = OnlySql();
        Assert.Contains(version == 0
            ? "\"dischargepower\""
            : $"\"{Physical(version)}\" AS \"dischargepower\"", sql);
        // The field filter targets the real (versioned) column.
        Assert.Matches($"WHERE.*\"{Physical(version)}\"\\s*>", sql.Replace("\n", " "));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Aggregation_Standard_AliasesTheLogicalName(int version)
    {
        Register(version);
        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(BatteryType)
            .WithAggregationColumns([
                new AggregationColumn("DischargePower", AggregationFunction.Average),
                new AggregationColumn("DischargePower", AggregationFunction.Maximum)])
            .WithTimeRange(From, To);

        await NewSut().ExecuteAggregationQueryAsync(RawArchive, options);

        var sql = OnlySql();
        Assert.Contains($"(\"{Physical(version)}\")", sql);
        Assert.Contains("AS \"Avg_dischargepower\"", sql);
        Assert.Contains("AS \"Max_dischargepower\"", sql);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Aggregation_IntegralAndStateDuration_AliasTheLogicalName(int version)
    {
        Register(version);
        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(BatteryType)
            .WithAggregationColumns([
                new AggregationColumn("DischargePower", AggregationFunction.Integral),
                new AggregationColumn("DischargePower", AggregationFunction.TimeWeightedAverage),
                new AggregationColumn("DischargePower", AggregationFunction.StateDuration, "0"),
                new AggregationColumn("DischargePower", AggregationFunction.StateDuration, "100")])
            .WithTimeRange(From, To);

        await NewSut().ExecuteAggregationQueryAsync(RawArchive, options);

        var sql = OnlySql();
        Assert.Contains("AS \"dischargepower_integral\"", sql);
        Assert.Contains("AS \"dischargepower_twavg\"", sql);
        Assert.Contains("AS \"dischargepower_stateduration_0\"", sql);
        Assert.Contains("AS \"dischargepower_stateduration_100\"", sql);
        Assert.Contains($"\"{Physical(version)}\"", sql);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task TimeRangeArchive_InlineTimeWeightedAverage_AliasesTheLogicalName(int version)
    {
        Register(version);
        var plain = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(BatteryType)
            .WithAggregationColumns([new AggregationColumn("DischargePower", AggregationFunction.TimeWeightedAverage)])
            .WithTimeRange(From, To);
        var grouped = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(BatteryType)
            .WithAggregationColumns([new AggregationColumn("DischargePower", AggregationFunction.TimeWeightedAverage)])
            .WithGroupByColumns(["rtId"])
            .WithTimeRange(From, To);

        await NewSut().ExecuteAggregationQueryAsync(TimeRangeArchive, plain);
        await NewSut().ExecuteGroupedAggregationQueryAsync(TimeRangeArchive, grouped);

        Assert.Equal(2, _executedSql.Count);
        Assert.All(_executedSql, sql =>
        {
            Assert.DoesNotMatch(VersionedAlias, sql);
            Assert.Contains("AS \"dischargepower_twavg\"", sql);
            Assert.Contains($"\"{Physical(version)}\" IS NOT NULL", sql);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task GroupedAggregation_AliasesTheLogicalName_AlsoOnTheLocfPath(int version)
    {
        Register(version);
        var plain = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(BatteryType)
            .WithAggregationColumns([new AggregationColumn("DischargePower", AggregationFunction.Sum)])
            .WithGroupByColumns(["rtId"])
            .WithTimeRange(From, To);
        var locf = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(BatteryType)
            .WithAggregationColumns([new AggregationColumn("DischargePower", AggregationFunction.Integral)])
            .WithGroupByColumns(["DischargePower"]) // a computed column as grouping key
            .WithTimeRange(From, To);

        await NewSut().ExecuteGroupedAggregationQueryAsync(RawArchive, plain);
        await NewSut().ExecuteGroupedAggregationQueryAsync(RawArchive, locf);

        Assert.Equal(2, _executedSql.Count);
        Assert.All(_executedSql, sql => Assert.DoesNotMatch(VersionedAlias, sql));
        Assert.Contains("AS \"Sum_dischargepower\"", _executedSql[0]);
        Assert.Contains("AS \"dischargepower_integral\"", _executedSql[1]);
        Assert.Contains($"\"{Physical(version)}\"", _executedSql[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Downsampling_AliasesTheLogicalName_OnRawAndTimeRangeArchives(int version)
    {
        Register(version);
        foreach (var archive in new[] { RawArchive, TimeRangeArchive })
        {
            var options = StreamDataDownsamplingQueryOptions.Create()
                .WithCkTypeId(BatteryType)
                .WithAggregationColumns([new AggregationColumn("DischargePower", AggregationFunction.Average)])
                .WithFieldFilters([new FieldFilter("DischargePower", FieldFilterOperator.GreaterThan, "0")])
                .WithTimeRange(From, To)
                .WithLimit(24);
            _executedSql.Clear();

            await NewSut().ExecuteDownsamplingQueryAsync(archive, options);

            Assert.NotEmpty(_executedSql);
            Assert.All(_executedSql, sql => Assert.DoesNotMatch(VersionedAlias, sql));
            Assert.Contains(_executedSql, sql => sql.Contains("AS \"Avg_dischargepower\"", StringComparison.Ordinal));
            Assert.Contains(_executedSql, sql => sql.Contains($"\"{Physical(version)}\"", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(0, "dischargepower")]
    [InlineData(2, "dischargepower__v2")]
    public void Resolver_KeepsTheLogicalKey_AndExposesThePhysicalColumnSeparately(int version, string physical)
    {
        var snapshot = new ArchiveSnapshot(RawArchive, BatteryType, CkArchiveStatus.Activated, "raw", Columns(version));

        var resolved = StreamDataFieldResolver.CreateForArchive(snapshot).Resolve("dischargepower");

        Assert.NotNull(resolved);
        Assert.Equal("dischargepower", resolved.CrateDbName);
        Assert.Equal("dischargepower", resolved.GraphQlAlias);
        Assert.Equal(physical, resolved.PhysicalName);
    }

    [Theory]
    [InlineData(0, "dischargepower")]
    [InlineData(1, "dischargepower__v1")]
    [InlineData(2, "dischargepower__v2")]
    public void RollupSourceColumn_ReadsTheActiveVersionOfAComputedSource_ButKeepsTheLogicalTargetNames(
        int version, string expectedSource)
    {
        var source = new ArchiveSnapshot(TimeRangeArchive, BatteryType, CkArchiveStatus.Activated, "tr", Columns(version))
        { IsTimeRange = true, Period = TimeSpan.FromMinutes(15) };
        var spec = new CkRollupAggregationSpec("DischargePower", CkRollupFunction.Avg, null);

        var bound = RollupAggregationColumns.ResolveForSource(
            spec, source, null, OctoObjectId.GenerateNewId());

        Assert.All(bound.Columns, c => Assert.Equal(expectedSource, c.SourceColumn));
        Assert.Equal(["dischargepower_avg_sum", "dischargepower_avg_count"], bound.Columns.Select(c => c.ColumnName));
    }
}
