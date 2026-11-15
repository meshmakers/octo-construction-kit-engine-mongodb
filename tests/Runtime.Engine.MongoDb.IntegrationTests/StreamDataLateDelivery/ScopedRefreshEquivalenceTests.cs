using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataLateDelivery;

/// <summary>
///     AB#9016 against a real CrateDB: the in-place refresh of a late delivery gives the readers exactly what a full-day
///     recompute gives them, for every series - the late ones and the untouched ones - on both levels of a ladder
///     (hourly rollup over a raw archive, daily rollup over the hourly one), including the TimeWeightedAvg carry bucket
///     after the late day. Two tenants are built identically; one is refreshed in place, the other recomputed in full, and
///     the rows the readers select (generation predicate of the pointer table) are compared.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class ScopedRefreshEquivalenceTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));
    private static readonly DateTime Day1 = new(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = Day1.AddDays(1);
    private static readonly DateTime Day3 = Day1.AddDays(2);
    private static readonly OctoObjectId[] Meters = Enumerable.Range(0, 5).Select(_ => OctoObjectId.GenerateNewId()).ToArray();

    private static readonly ArchiveColumnDdl[] ColumnDdl =
    [
        new("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)
    ];

    private static readonly CkRollupAggregationSpec[] Specs =
    [
        new("Value", CkRollupFunction.Sum, "value_sum"),
        new("Value", CkRollupFunction.TimeWeightedAvg, "value_twa"),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Rung(ArchiveSnapshot Archive, RollupArchiveSnapshot Rollup)
    {
        public OctoObjectId RtId => Archive.RtId;
    }

    private sealed record Ladder(string Tenant, ArchiveSnapshot Base, Rung Hourly, Rung Daily, CrateDbStreamDataRepository Repository)
    {
        public string Table(OctoObjectId rtId) => TenantSchema.QualifiedArchiveTable(Tenant, rtId.ToString());

        public string GenMap(OctoObjectId rtId) => GenerationMapSqlBuilder.GenMapTable(Tenant, rtId.ToString());
    }

    private static Rung NewRung(OctoObjectId baseRtId, OctoObjectId sourceRtId, TimeSpan bucket, OctoObjectId rtId)
    {
        _ = baseRtId;
        return new Rung(
            new ArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, null, RollupColumnGenerator.Generate(Specs))
            {
                RollupAggregations = Specs,
                Period = bucket
            },
            new RollupArchiveSnapshot(rtId, Type, CkArchiveStatus.Activated, null,
                [new RollupSourceReference(sourceRtId)], bucket, TimeSpan.FromMinutes(5), null, Specs, null));
    }

    /// <summary>
    ///     A tenant with the same archive ids, data and history as every other one built by this method: raw readings of
    ///     five meters every 30 minutes over two days, forward-aggregated into both rollups (generation 0), the second
    ///     day of the hourly rollup recomputed (generation 1, unscoped) and the first day recomputed for meter 3 alone
    ///     (generation 2, scoped) - so the refresh meets rows of generation 0, of an unscoped and of a foreign scoped
    ///     pointer entry.
    /// </summary>
    private async Task<Ladder> CreateLadderAsync(OctoObjectId baseRtId, OctoObjectId hourlyRtId, OctoObjectId dailyRtId)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var baseSnapshot = new ArchiveSnapshot(baseRtId, Type, CkArchiveStatus.Activated, "readings",
            ColumnDdl.Select(c => new CkArchiveColumnSpec(c.Path, false, false)).ToArray());
        var hourly = NewRung(baseRtId, baseRtId, TimeSpan.FromHours(1), hourlyRtId);
        var daily = NewRung(baseRtId, hourlyRtId, TimeSpan.FromDays(1), dailyRtId);

        var archives = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => archives.GetAsync(baseRtId)).Returns(baseSnapshot);
        A.CallTo(() => archives.GetAsync(hourly.RtId)).Returns(hourly.Archive);
        A.CallTo(() => archives.GetAsync(daily.RtId)).Returns(daily.Archive);
        var rollups = A.Fake<IRollupArchiveRuntimeStore>();
        A.CallTo(() => rollups.GetAsync(hourly.RtId)).Returns(hourly.Rollup);
        A.CallTo(() => rollups.GetAsync(daily.RtId)).Returns(daily.Rollup);

        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, archives, A.Fake<IFormulaEngine>(),
            rollupArchiveStore: rollups, recomputeProcessState: new RecomputeProcessState());
        var ladder = new Ladder(tenant, baseSnapshot, hourly, daily, repository);

        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(ladder.Table(baseRtId), ColumnDdl, 1, 0));
        foreach (var rung in new[] { hourly, daily })
        {
            await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateWindowedTable(ladder.Table(rung.RtId),
                RollupColumnTypeResolver.Resolve(rung.Archive.Columns, rung.Rollup.Aggregations), 1, 0, includeGeneration: true));
            await fixture.SqlAsync(GenerationMapSqlBuilder.BuildCreateTable(ladder.GenMap(rung.RtId)));
        }

        var rows = new List<string>();
        for (var m = 0; m < Meters.Length; m++)
        {
            for (var i = 0; i < 96; i++)
            {
                // Meter 2 has no reading at midnight between the two days, so the first bucket of day 2 opens with the
                // LOCF carry from its last reading of day 1 - the carry bucket the late delivery changes.
                if (m == 1 && i == 48)
                {
                    continue;
                }

                rows.Add(Row(Meters[m], Day1.AddMinutes(30 * i), m * 10 + i % 7));
            }
        }

        await UpsertRawAsync(ladder, rows);

        for (var hour = 0; hour < 48; hour++)
        {
            await repository.AggregateBucketAsync(baseSnapshot, hourly.Rollup, Day1.AddHours(hour), Day1.AddHours(hour + 1), Ct);
        }

        await fixture.SqlAsync($"REFRESH TABLE {ladder.Table(hourly.RtId)}");
        await repository.AggregateBucketAsync(hourly.Archive, daily.Rollup, Day1, Day2, Ct);
        await repository.AggregateBucketAsync(hourly.Archive, daily.Rollup, Day2, Day3, Ct);

        await repository.ExecuteAsync(baseSnapshot, hourly.Rollup, Day2, Day3, null, Ct);
        await repository.ExecuteAsync(baseSnapshot, hourly.Rollup, Day1, Day2, Meters[2], Ct);
        return ladder;
    }

    private static string Row(OctoObjectId meter, DateTime timestamp, double value) =>
        FormattableString.Invariant(
            $"('{meter}', {new DateTimeOffset(timestamp).ToUnixTimeMilliseconds()}, '{Type.SemanticVersionedFullName}', {value})");

    private async Task UpsertRawAsync(Ladder ladder, IReadOnlyList<string> rows)
    {
        var table = ladder.Table(ladder.Base.RtId);
        await fixture.SqlAsync(
            $"INSERT INTO {table} (\"rtid\", \"timestamp\", \"cktypeid\", \"value\") VALUES {string.Join(", ", rows)} " +
            "ON CONFLICT (\"timestamp\", \"rtid\", \"cktypeid\") DO UPDATE SET \"value\" = EXCLUDED.\"value\"");
        await fixture.SqlAsync($"REFRESH TABLE {table}");
    }

    /// <summary>
    ///     The late delivery: meter 1 gets a new reading in the morning and one just before midnight, meter 2 a corrected
    ///     value at 23:30 - the last reading of the day, which is the carry-in of the first bucket of the next day.
    /// </summary>
    private Task DeliverLateAsync(Ladder ladder) => UpsertRawAsync(ladder,
    [
        Row(Meters[0], Day1.AddHours(8).AddMinutes(15), 100),
        Row(Meters[0], Day1.AddHours(23).AddMinutes(45), 70),
        Row(Meters[1], Day1.AddHours(23).AddMinutes(30), 50),
    ]);

    /// <summary>Every row a reader selects from the rollup, as text: the generation predicate of its pointer table.</summary>
    private async Task<List<string>> ReaderViewAsync(Ladder ladder, Rung rung)
    {
        var table = ladder.Table(rung.RtId);
        var genMap = ladder.GenMap(rung.RtId);
        await fixture.SqlAsync($"REFRESH TABLE {table}");
        await fixture.SqlAsync($"REFRESH TABLE {genMap}");
        var pointers = (await fixture.QueryAsync($"SELECT \"range_start\", \"range_end\", \"rtid_scope\", \"generation\" FROM {genMap}"))
            .Select(r => new GenerationRange(Convert.ToInt64(r["range_start"]), Convert.ToInt64(r["range_end"]),
                r["rtid_scope"]?.ToString() ?? string.Empty, Convert.ToInt64(r["generation"])))
            .ToList();

        var schemaName = table.Split('.')[0].Trim('"');
        var tableName = table.Split('.')[1].Trim('"');
        var columns = (await fixture.QueryAsync(
                $"SELECT column_name FROM information_schema.columns WHERE table_schema = '{schemaName}' AND table_name = '{tableName}' ORDER BY column_name"))
            .Select(r => (string)r["column_name"]!)
            .Where(c => c is not ("generation" or "rtcreationdatetime" or "rtchangeddatetime"))
            .ToList();
        var select = string.Join(", ", columns.Select(c => $"\"{c}\""));
        var rows = await fixture.QueryAsync(
            $"SELECT CAST(\"window_start\" AS BIGINT) AS \"key_ms\", {select} FROM {table} WHERE {GenerationFilterSql.Render(pointers)} ORDER BY \"window_start\", \"rtid\"");
        return rows.Select(r => FormattableString.Invariant($"key={r["key_ms"]}|") +
                                string.Join("|", columns.Select(c => FormattableString.Invariant($"{c}={r[c]}")))).ToList();
    }

    private async Task<(long Entries, long MaxGeneration)> PointerStateAsync(Ladder ladder, Rung rung)
    {
        var genMap = ladder.GenMap(rung.RtId);
        await fixture.SqlAsync($"REFRESH TABLE {genMap}");
        return (await fixture.CountAsync($"SELECT COUNT(*) FROM {genMap}"),
            await fixture.CountAsync($"SELECT COALESCE(MAX(\"generation\"), 0) FROM {genMap}"));
    }

    [Fact]
    public async Task TheInPlaceRefresh_GivesTheReadersWhatAFullDayRecomputeGives_ForEverySeries_OnBothLevels()
    {
        var baseRtId = OctoObjectId.GenerateNewId();
        var hourlyRtId = OctoObjectId.GenerateNewId();
        var dailyRtId = OctoObjectId.GenerateNewId();
        var scoped = await CreateLadderAsync(baseRtId, hourlyRtId, dailyRtId);
        var full = await CreateLadderAsync(baseRtId, hourlyRtId, dailyRtId);
        Assert.Equal(await ReaderViewAsync(full, full.Hourly), await ReaderViewAsync(scoped, scoped.Hourly));
        var hourlyBefore = await ReaderViewAsync(scoped, scoped.Hourly);
        var pointersBefore = await PointerStateAsync(scoped, scoped.Hourly);

        await DeliverLateAsync(scoped);
        await DeliverLateAsync(full);

        // The range the orchestrator derives for the late rows: their buckets, plus the next bucket for the
        // TimeWeightedAvg carry (here the first hour of the next day); the daily rollup recomputes both days it touches.
        var late = RtIdSet.Of([Meters[0], Meters[1]])!;
        await scoped.Repository.ExecuteScopedRefreshAsync(scoped.Base, scoped.Hourly.Rollup, Day1.AddHours(8), Day2.AddHours(1), late, Ct);
        await scoped.Repository.ExecuteScopedRefreshAsync(scoped.Hourly.Archive, scoped.Daily.Rollup, Day1, Day3, late, Ct);

        await full.Repository.ExecuteAsync(full.Base, full.Hourly.Rollup, Day1.AddHours(8), Day2.AddHours(1), null, Ct);
        await full.Repository.ExecuteAsync(full.Hourly.Archive, full.Daily.Rollup, Day1, Day3, null, Ct);

        var hourlyAfter = await ReaderViewAsync(scoped, scoped.Hourly);
        Assert.Equal(await ReaderViewAsync(full, full.Hourly), hourlyAfter);
        Assert.Equal(await ReaderViewAsync(full, full.Daily), await ReaderViewAsync(scoped, scoped.Daily));

        // The late rows did change something (else the comparison proves nothing): the morning bucket of meter 1, and
        // the carry bucket at midnight for meter 2.
        Assert.NotEqual(hourlyBefore, hourlyAfter);
        var changed = hourlyBefore.Except(hourlyAfter).ToList();
        Assert.Contains(changed, r => r.Contains($"rtid={Meters[0]}", StringComparison.Ordinal));
        Assert.Contains(changed, r => r.Contains($"rtid={Meters[1]}", StringComparison.Ordinal)
                                      && r.StartsWith($"key={new DateTimeOffset(Day2).ToUnixTimeMilliseconds()}|", StringComparison.Ordinal));
        Assert.DoesNotContain(changed, r => Meters.Skip(2).Any(m => r.Contains($"rtid={m}", StringComparison.Ordinal)));

        // In place: no pointer entry was added, no generation was used.
        Assert.Equal(pointersBefore, await PointerStateAsync(scoped, scoped.Hourly));
    }

    [Fact]
    public async Task TheInPlaceRefresh_RemovesABucketWhoseSourceRowsAreGone_AsTheFullRecomputeDoes()
    {
        var baseRtId = OctoObjectId.GenerateNewId();
        var hourlyRtId = OctoObjectId.GenerateNewId();
        var dailyRtId = OctoObjectId.GenerateNewId();
        var scoped = await CreateLadderAsync(baseRtId, hourlyRtId, dailyRtId);
        var full = await CreateLadderAsync(baseRtId, hourlyRtId, dailyRtId);
        var firstBucket = new DateTimeOffset(Day1).ToUnixTimeMilliseconds();

        // Meter 1 loses both readings of the first hour of day 1 (its first readings at all, so no carry-in either): the
        // bucket has no source rows any more. Meter 2 gets a late reading in the second hour, so the set covers a series
        // whose bucket stays and one whose bucket disappears.
        foreach (var ladder in new[] { scoped, full })
        {
            var table = ladder.Table(ladder.Base.RtId);
            await fixture.SqlAsync(
                $"DELETE FROM {table} WHERE \"rtid\" = '{Meters[0]}' AND \"timestamp\" < {firstBucket + 3600000}");
            await fixture.SqlAsync($"REFRESH TABLE {table}");
            await UpsertRawAsync(ladder, [Row(Meters[1], Day1.AddHours(1).AddMinutes(10), 42)]);
        }

        Assert.Contains(await ReaderViewAsync(scoped, scoped.Hourly),
            r => r.StartsWith($"key={firstBucket}|", StringComparison.Ordinal) && r.Contains($"rtid={Meters[0]}", StringComparison.Ordinal));

        var set = RtIdSet.Of([Meters[0], Meters[1]])!;
        await scoped.Repository.ExecuteScopedRefreshAsync(scoped.Base, scoped.Hourly.Rollup, Day1, Day1.AddHours(3), set, Ct);
        await full.Repository.ExecuteAsync(full.Base, full.Hourly.Rollup, Day1, Day1.AddHours(3), null, Ct);

        var view = await ReaderViewAsync(scoped, scoped.Hourly);
        Assert.Equal(await ReaderViewAsync(full, full.Hourly), view);
        Assert.DoesNotContain(view,
            r => r.StartsWith($"key={firstBucket}|", StringComparison.Ordinal) && r.Contains($"rtid={Meters[0]}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheInPlaceRefresh_OfANeverRecomputedRollup_WritesGenerationZero_AndAddsNoPointer()
    {
        var scoped = await CreateLadderAsync(OctoObjectId.GenerateNewId(), OctoObjectId.GenerateNewId(), OctoObjectId.GenerateNewId());
        await fixture.SqlAsync($"REFRESH TABLE {scoped.Table(scoped.Daily.RtId)}");
        var dailyRowsBefore = await fixture.CountAsync($"SELECT COUNT(*) FROM {scoped.Table(scoped.Daily.RtId)}");
        Assert.Equal(Meters.Length * 2, dailyRowsBefore);

        await DeliverLateAsync(scoped);
        await scoped.Repository.ExecuteScopedRefreshAsync(
            scoped.Hourly.Archive, scoped.Daily.Rollup, Day1, Day3, RtIdSet.Of([Meters[0]])!, Ct);

        await fixture.SqlAsync($"REFRESH TABLE {scoped.Table(scoped.Daily.RtId)}");
        Assert.Equal(dailyRowsBefore, await fixture.CountAsync($"SELECT COUNT(*) FROM {scoped.Table(scoped.Daily.RtId)}"));
        Assert.Equal(0, await fixture.CountAsync($"SELECT COUNT(*) FROM {scoped.Table(scoped.Daily.RtId)} WHERE \"generation\" <> 0"));
        Assert.Equal((0L, 0L), await PointerStateAsync(scoped, scoped.Daily));
    }
}
