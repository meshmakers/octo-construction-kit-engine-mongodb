using System.Globalization;
using System.Text;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using StreamDataException = Meshmakers.Octo.Runtime.Contracts.StreamData.StreamDataException;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataIntegral;

/// <summary>
///     AB#7918: <c>Integral</c> over a raw archive against a real CrateDB, through
///     <see cref="CrateDbStreamDataRepository" /> (routing, the LOCF statement of
///     <c>TimeWeightedQuerySqlBuilder</c> and the row mapping). Every fixture has an analytically known
///     result in value × hours (kW → kWh): step integration, the last value holds until the next row of the
///     same rtId or the window end, the last row before the window opens it, rtIds are summed, an empty
///     window is <c>null</c>. The meter check reproduces the 1 % comparison of AB#7692 from recorded rows.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class StreamDataIntegralIntegrationTests(CrateStagingCopyFixture fixture)
{
    private const double Tolerance = 1e-9;
    private static readonly RtCkId<CkTypeId> MeterType = new("Test", new CkTypeId("Meter"));
    private const string MeterTypeLiteral = "Test/Meter";

    /// <summary>The window of most fixtures: one hour, so a constant power of P kW integrates to P kWh.</summary>
    private static readonly DateTime From = new(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime To = From.AddHours(1);

    private sealed record Row(string RtId, DateTime Timestamp, double? ActivePower, string Site = "A")
    {
        public double? ImportPower => ActivePower is { } p ? Math.Max(p, 0) : null;
    }

    private sealed class Archive(string tenantId, OctoObjectId rtId, CrateDbStreamDataRepository repository)
    {
        public string TenantId { get; } = tenantId;
        public OctoObjectId RtId { get; } = rtId;
        public CrateDbStreamDataRepository Repository { get; } = repository;
    }

    private async Task<Archive> CreateArchiveAsync(IEnumerable<Row> rows)
    {
        var tenantId = CrateStagingCopyFixture.NewTenantId();
        var archiveRtId = OctoObjectId.GenerateNewId();
        var table = TenantSchema.QualifiedArchiveTable(tenantId, archiveRtId.ToString());
        var dbl = new CrateColumnType.Primitive("DOUBLE PRECISION");
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(table,
        [
            new ArchiveColumnDdl("ActivePower", dbl, false, true),
            new ArchiveColumnDdl("ImportPower", dbl, false, true),
            new ArchiveColumnDdl("Site", new CrateColumnType.Primitive("TEXT"), false, true)
        ], numberOfShards: 2, numberOfReplicas: 0));

        foreach (var chunk in rows.Chunk(500))
        {
            var sql = new StringBuilder(
                $"INSERT INTO {table} (\"rtid\", \"timestamp\", \"cktypeid\", \"activepower\", \"importpower\", \"site\") VALUES ");
            sql.AppendJoin(", ", chunk.Select(r =>
                $"('{r.RtId}', '{r.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}', '{MeterTypeLiteral}', " +
                $"{Literal(r.ActivePower)}, {Literal(r.ImportPower)}, '{r.Site}')"));
            await fixture.SqlAsync(sql.ToString());
        }

        await fixture.SqlAsync($"REFRESH TABLE {table}");

        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(archiveRtId)).Returns(new ArchiveSnapshot(archiveRtId, MeterType,
            CkArchiveStatus.Activated, "raw",
            [
                new CkArchiveColumnSpec("ActivePower", true, false),
                new CkArchiveColumnSpec("ImportPower", true, false),
                new CkArchiveColumnSpec("Site", true, false)
            ]));
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenantId, store,
            A.Fake<IFormulaEngine>());
        return new Archive(tenantId, archiveRtId, repository);
    }

    private static string Literal(double? value) =>
        value is { } v ? v.ToString("R", CultureInfo.InvariantCulture) : "NULL";

    private static string NewRtId() => OctoObjectId.GenerateNewId().ToString();

    /// <summary>One row every <paramref name="step" /> in [start, end) with a constant value.</summary>
    private static IEnumerable<Row> Every(string rtId, TimeSpan step, DateTime start, DateTime end, double value, string site = "A")
    {
        for (var t = start; t < end; t += step)
        {
            yield return new Row(rtId, t, value, site);
        }
    }

    private static async Task<StreamDataRow> AggregateAsync(Archive archive, DateTime? from, DateTime? to,
        IReadOnlyList<OctoObjectId>? rtIds, params AggregationColumn[] columns)
    {
        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(MeterType)
            .WithAggregationColumns(columns)
            .WithTimeRange(from, to)
            .WithRtIds(rtIds);
        var result = await archive.Repository.ExecuteAggregationQueryAsync(archive.RtId, options);
        return Assert.Single(result.Rows);
    }

    private static Task<StreamDataRow> IntegralAsync(Archive archive, string path = "ActivePower",
        DateTime? from = null, DateTime? to = null, IReadOnlyList<OctoObjectId>? rtIds = null) =>
        AggregateAsync(archive, from ?? From, to ?? To, rtIds, new AggregationColumn(path, AggregationFunction.Integral));

    private static double? Value(StreamDataRow row, string key) =>
        row.Values[key] is { } v ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : null;

    [Fact]
    public async Task UniformRows_Every5sAndEvery10s_GiveTheSameIntegral_ForTheSamePower()
    {
        // 2 kW for one hour = 2 kWh, whatever the cadence. SUM × 10/3600 would give 4 kWh for the 5-s rows.
        var fiveSeconds = NewRtId();
        var tenSeconds = NewRtId();
        var archive = await CreateArchiveAsync(Every(fiveSeconds, TimeSpan.FromSeconds(5), From, To, 2.0)
            .Concat(Every(tenSeconds, TimeSpan.FromSeconds(10), From, To, 2.0)));

        var at5 = Value(await IntegralAsync(archive, rtIds: [OctoObjectId.Parse(fiveSeconds)]), "activepower_integral");
        var at10 = Value(await IntegralAsync(archive, rtIds: [OctoObjectId.Parse(tenSeconds)]), "activepower_integral");

        Assert.Equal(2.0, at5!.Value, Tolerance);
        Assert.Equal(2.0, at10!.Value, Tolerance);
    }

    [Fact]
    public async Task RowsWrittenOnChange_AreWeightedByHowLongEachValueHeld()
    {
        // 1 kW for 7 min, 3 kW for 13 min, 0 kW for 31 min, 4 kW for the last 9 min:
        // (1·7 + 3·13 + 0·31 + 4·9) / 60 = 82/60 kWh.
        var rt = NewRtId();
        var archive = await CreateArchiveAsync(
        [
            new Row(rt, From, 1.0),
            new Row(rt, From.AddMinutes(7), 3.0),
            new Row(rt, From.AddMinutes(20), 0.0),
            new Row(rt, From.AddMinutes(51), 4.0)
        ]);

        Assert.Equal(82.0 / 60.0, Value(await IntegralAsync(archive), "activepower_integral")!.Value, Tolerance);
    }

    [Fact]
    public async Task AGapInsideTheWindow_HoldsTheLastValueUntilTheNextRow()
    {
        // 2 kW every 10 s until 10:15, no row until 10:45, then 1 kW every 10 s:
        // the last 2 kW row holds across the gap, so 2 kW · 45 min + 1 kW · 15 min = 1.75 kWh.
        var rt = NewRtId();
        var archive = await CreateArchiveAsync(
            Every(rt, TimeSpan.FromSeconds(10), From, From.AddMinutes(15), 2.0)
                .Concat(Every(rt, TimeSpan.FromSeconds(10), From.AddMinutes(45), To, 1.0)));

        Assert.Equal(1.75, Value(await IntegralAsync(archive), "activepower_integral")!.Value, Tolerance);
    }

    [Fact]
    public async Task AValueBeforeTheWindow_OpensIt_AndPlainColumnsStillIgnoreIt()
    {
        // 4 kW written at 09:59 holds until the row at 10:30 (2 kW): 4 · 0.5 + 2 · 0.5 = 3 kWh.
        // A Count in the same query sees only the one row inside the window.
        var rt = NewRtId();
        var archive = await CreateArchiveAsync(
        [
            new Row(rt, From.AddMinutes(-1), 4.0),
            new Row(rt, From.AddMinutes(30), 2.0)
        ]);

        var row = await AggregateAsync(archive, From, To, null,
            new AggregationColumn("ActivePower", AggregationFunction.Integral),
            new AggregationColumn("ActivePower", AggregationFunction.Count),
            new AggregationColumn("ActivePower", AggregationFunction.TimeWeightedAverage));

        Assert.Equal(3.0, Value(row, "activepower_integral")!.Value, Tolerance);
        Assert.Equal(1.0, Value(row, "activepower_count")!.Value, Tolerance);
        // Same statement, same weighting: TWA × window length = integral.
        Assert.Equal(3.0, Value(row, "activepower_twavg")!.Value, Tolerance);
    }

    [Fact]
    public async Task OnlyACarryRow_GivesCarryTimesTheWindow()
    {
        var rt = NewRtId();
        var archive = await CreateArchiveAsync([new Row(rt, From.AddMinutes(-30), 3.0)]);

        Assert.Equal(3.0, Value(await IntegralAsync(archive), "activepower_integral")!.Value, Tolerance);
        // Half the window holds half the energy.
        Assert.Equal(1.5, Value(await IntegralAsync(archive, to: From.AddMinutes(30)), "activepower_integral")!.Value, Tolerance);
    }

    [Fact]
    public async Task AnEmptyWindowWithoutCarry_IsNull_NotZero()
    {
        // Rows exist, but only after the window: no row in it and none before it.
        var rt = NewRtId();
        var archive = await CreateArchiveAsync(Every(rt, TimeSpan.FromMinutes(1), To.AddMinutes(30), To.AddHours(1), 5.0));

        var row = await IntegralAsync(archive);

        Assert.Null(Value(row, "activepower_integral"));
    }

    [Fact]
    public async Task NullValues_ContributeNothingForTheirInterval()
    {
        // 2 kW for 30 min, NULL for 15 min, 4 kW for 15 min: 1 + 0 + 1 = 2 kWh.
        var rt = NewRtId();
        var archive = await CreateArchiveAsync(
        [
            new Row(rt, From, 2.0),
            new Row(rt, From.AddMinutes(30), null),
            new Row(rt, From.AddMinutes(45), 4.0)
        ]);

        Assert.Equal(2.0, Value(await IntegralAsync(archive), "activepower_integral")!.Value, Tolerance);
    }

    [Fact]
    public async Task SeveralEntities_AreIntegratedEachOnItsOwn_AndSummed()
    {
        // rt1: 2 kW every 5 s; rt2: 1 kW every 10 s, opened by a carry row 2 s before the window.
        // LOCF never crosses entities: 2 + 1 = 3 kWh.
        var rt1 = NewRtId();
        var rt2 = NewRtId();
        var archive = await CreateArchiveAsync(Every(rt1, TimeSpan.FromSeconds(5), From, To, 2.0)
            .Append(new Row(rt2, From.AddSeconds(-2), 1.0))
            .Concat(Every(rt2, TimeSpan.FromSeconds(10), From.AddSeconds(8), To, 1.0)));

        Assert.Equal(3.0, Value(await IntegralAsync(archive), "activepower_integral")!.Value, Tolerance);
        Assert.Equal(3.0, Value(await IntegralAsync(archive,
            rtIds: [OctoObjectId.Parse(rt1), OctoObjectId.Parse(rt2)]), "activepower_integral")!.Value, Tolerance);
        Assert.Equal(1.0, Value(await IntegralAsync(archive, rtIds: [OctoObjectId.Parse(rt2)]), "activepower_integral")!.Value,
            Tolerance);
    }

    [Fact]
    public async Task GroupedQuery_SumsTheEntitiesOfEachGroup()
    {
        var rt1 = NewRtId();
        var rt2 = NewRtId();
        var rt3 = NewRtId();
        var archive = await CreateArchiveAsync(Every(rt1, TimeSpan.FromSeconds(5), From, To, 2.0, "A")
            .Concat(Every(rt2, TimeSpan.FromSeconds(10), From, To, 1.0, "A"))
            .Concat(Every(rt3, TimeSpan.FromMinutes(1), From, To, 0.5, "B")));

        var options = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(MeterType)
            .WithAggregationColumns([new AggregationColumn("ActivePower", AggregationFunction.Integral)])
            .WithGroupByColumns(["Site"])
            .WithTimeRange(From, To);
        var result = await archive.Repository.ExecuteGroupedAggregationQueryAsync(archive.RtId, options);

        var bySite = result.Rows.ToDictionary(r => (string)r.Values["site"]!, r => Value(r, "activepower_integral")!.Value);
        Assert.Equal(2, bySite.Count);
        Assert.Equal(3.0, bySite["A"], Tolerance);
        Assert.Equal(0.5, bySite["B"], Tolerance);
    }

    [Fact]
    public async Task AMissingFromOrTo_IsAStructuredError()
    {
        var archive = await CreateArchiveAsync([new Row(NewRtId(), From, 1.0)]);

        var ex = await Assert.ThrowsAsync<StreamDataException>(() =>
            AggregateAsync(archive, From, null, null, new AggregationColumn("ActivePower", AggregationFunction.Integral)));
        Assert.Equal(archive.RtId, ex.ArchiveRtId);
        await Assert.ThrowsAsync<StreamDataException>(() =>
            AggregateAsync(archive, null, To, null, new AggregationColumn("ActivePower", AggregationFunction.Integral)));
    }

    /// <summary>
    ///     The 1 % meter check from recorded grid rows of one site (timestamps and signed ActivePower only, one row about
    ///     every 5 s), against the delta of the meter register <c>ImportedEnergy</c> over the same window:
    ///     <list type="bullet">
    ///         <item><c>grid-5s-meter-check-ab7692.csv</c>: the AB#7692 window 2026-10-30 10:30:10.7Z to 11:15:45.8Z
    ///         (45.6 min, 548 rows), register delta 0.924 kWh; integral 0.9297 kWh = +0.6 %, SUM × 10/3600 = 1.862 kWh = +101 %.</item>
    ///         <item><c>grid-5s-meter-check-20261030-evening.csv</c>: a fresh window 18:44:01.391Z to 19:31:39.715Z (47.6 min,
    ///         571 rows) bounded by two register reads (14267.023 → 14267.220 kWh, delta 0.197 kWh, register resolution
    ///         0.001 kWh); integral 0.1976 kWh = +0.3 %, SUM × 10/3600 = 0.394 kWh = +100 %.</item>
    ///     </list>
    ///     The import is the integral of the positive part, which the archive keeps in its own column (on a real archive a
    ///     computed column <c>if(ActivePower &gt; 0, ActivePower, 0)</c>; a field filter <c>ActivePower &gt; 0</c> would carry
    ///     the last positive value across export periods instead). The board formula used until now, SUM(kW) × 10/3600,
    ///     is about twice the register delta because the rows come every 5 s and not every 10 s.
    /// </summary>
    [Theory]
    [InlineData("grid-5s-meter-check-ab7692.csv", "2026-10-30T10:30:10.700Z", "2026-10-30T11:15:45.800Z", 548, 0.924, 0.9297)]
    [InlineData("grid-5s-meter-check-20261030-evening.csv", "2026-10-30T18:44:01.391Z", "2026-10-30T19:31:39.715Z", 571, 0.197, 0.1976)]
    public async Task RecordedGridRows_IntegrateToTheMeterRegisterDelta_Within1Percent(
        string fixtureFile, string windowFrom, string windowTo, int rowsInWindow, double registerDeltaKWh, double expectedKWh)
    {
        var rt = NewRtId();
        var rows = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "testData", "integral", fixtureFile))
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("timestamp", StringComparison.Ordinal))
            .Select(l => l.Split(','))
            .Select(p => new Row(rt, ParseUtc(p[0]), double.Parse(p[1], CultureInfo.InvariantCulture)))
            .ToList();
        var from = ParseUtc(windowFrom);
        var to = ParseUtc(windowTo);
        Assert.Equal(rowsInWindow, rows.Count(r => r.Timestamp >= from && r.Timestamp < to));
        Assert.Contains(rows, r => r.Timestamp < from); // the carry-in row is part of the fixture
        var archive = await CreateArchiveAsync(rows);

        var row = await AggregateAsync(archive, from, to, null,
            new AggregationColumn("ImportPower", AggregationFunction.Integral),
            new AggregationColumn("ImportPower", AggregationFunction.Sum));

        var integral = Value(row, "importpower_integral")!.Value;
        var deviation = (integral - registerDeltaKWh) / registerDeltaKWh;
        Assert.True(Math.Abs(deviation) <= 0.01,
            $"Integral {integral:F4} kWh deviates {deviation:P2} from the register delta {registerDeltaKWh} kWh (limit 1 %).");
        Assert.Equal(expectedKWh, integral, 0.0001);

        var tenSecondFormula = Value(row, "importpower_sum")!.Value * 10 / 3600;
        Assert.True(tenSecondFormula / registerDeltaKWh > 1.9,
            $"SUM × 10/3600 = {tenSecondFormula:F3} kWh is expected to be about twice the register delta on 5-s rows.");
    }

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
