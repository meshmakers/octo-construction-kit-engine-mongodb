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

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataDownsampling;

/// <summary>
///     AB#7659: downsampling a TimeRange (windowed) archive with data gaps must return buckets of exactly the
///     requested width. The bin width used to be derived from <c>COUNT(DISTINCT window_start)</c>, so every gap made
///     it drift (367 buckets of 85,929 s instead of 365 of 86,400 s, counts of 87/94/95 instead of 96).
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class TimeRangeDownsamplingGapIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> MeterType = new("Test", new CkTypeId("Meter"));
    private const string MeterTypeLiteral = "Test/Meter";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private static readonly DateTime Year = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Archive(OctoObjectId RtId, CrateDbStreamDataRepository Repository);

    private async Task<Archive> CreateArchiveAsync(IReadOnlyCollection<DateTime> windowStarts, TimeSpan window)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rtId = OctoObjectId.GenerateNewId();
        var table = TenantSchema.QualifiedArchiveTable(tenant, rtId.ToString());
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateWindowedTable(table,
            [new ArchiveColumnDdl("Amount", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, true)], 2, 0));

        var rtEntity = OctoObjectId.GenerateNewId().ToString();
        foreach (var chunk in windowStarts.Chunk(500))
        {
            var sql = new StringBuilder(
                $"INSERT INTO {table} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"amount\") VALUES ");
            sql.AppendJoin(", ", chunk.Select(w =>
                $"('{Iso(w)}', '{Iso(w + window)}', '{rtEntity}', '{MeterTypeLiteral}', 1.0)"));
            await fixture.SqlAsync(sql.ToString());
        }

        await fixture.SqlAsync($"REFRESH TABLE {table}");

        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(rtId)).Returns(new ArchiveSnapshot(rtId, MeterType,
            CkArchiveStatus.Activated, null, [new CkArchiveColumnSpec("Amount", true, false)])
        {
            IsTimeRange = true,
            Period = window
        });
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, store,
            A.Fake<IFormulaEngine>());
        return new Archive(rtId, repository);
    }

    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static async Task<List<(DateTime Start, long Count)>> CountAsync(
        Archive archive, DateTime from, DateTime to, int limit)
    {
        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(MeterType)
            .WithAggregationColumns([new AggregationColumn("Amount", AggregationFunction.Count)])
            .WithTimeRange(from, to)
            .WithLimit(limit);
        var result = await archive.Repository.ExecuteDownsamplingQueryAsync(archive.RtId, options);
        return result.Rows
            .Select(r => (
                Start: DateTime.SpecifyKind(Convert.ToDateTime(r.Values["timestamp"], CultureInfo.InvariantCulture),
                    DateTimeKind.Utc),
                Count: r.Values["amount_count"] is { } c ? Convert.ToInt64(c, CultureInfo.InvariantCulture) : 0L))
            .OrderBy(r => r.Start)
            .ToList();
    }

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task OneYearOf15MinuteWindowsWithGaps_Limit365_GivesDailyBucketsOfExactly86400Seconds()
    {
        // Every day of 2026 has 96 windows except: a 6-hour gap on Mar 3 (24 windows missing), a whole
        // missing day on Jun 10 and a 3-hour gap on Sep 20 (12 windows missing).
        var starts = new List<DateTime>();
        for (var t = Year; t < Year.AddYears(1); t += Window)
        {
            var inGap = (t.Month == 3 && t.Day == 3 && t.Hour is >= 6 and < 12)
                        || (t.Month == 6 && t.Day == 10)
                        || (t.Month == 9 && t.Day == 20 && t.Hour is >= 14 and < 17);
            if (!inGap) starts.Add(t);
        }

        var archive = await CreateArchiveAsync(starts, Window);
        var buckets = await CountAsync(archive, Year, Year.AddYears(1), 365);

        Assert.Equal(365, buckets.Count);
        for (var i = 0; i < buckets.Count; i++)
        {
            Assert.Equal(Year.AddDays(i), buckets[i].Start);
        }

        Assert.Equal(72, buckets.Single(b => b.Start == Day(3, 3)).Count);
        Assert.Equal(0, buckets.Single(b => b.Start == Day(6, 10)).Count);
        Assert.Equal(84, buckets.Single(b => b.Start == Day(9, 20)).Count);
        var gapDays = new[] { Day(3, 3), Day(6, 10), Day(9, 20) };
        Assert.All(buckets.Where(b => !gapDays.Contains(b.Start)), b => Assert.Equal(96, b.Count));
        Assert.Equal(starts.Count, buckets.Sum(b => b.Count));
    }

    [Fact]
    public async Task TwentyFourHourlyBucketsOverARangeWithOnly9HoursOfData_GivesTwentyFourBucketsOf60Minutes()
    {
        var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var starts = new List<DateTime>();
        for (var t = from.AddHours(8); t < from.AddHours(17); t += Window) starts.Add(t);

        var archive = await CreateArchiveAsync(starts, Window);
        var buckets = await CountAsync(archive, from, from.AddHours(24), 24);

        Assert.Equal(24, buckets.Count);
        for (var i = 0; i < 24; i++)
        {
            Assert.Equal(from.AddHours(i), buckets[i].Start);
            Assert.Equal(i is >= 8 and < 17 ? 4 : 0, buckets[i].Count);
        }
    }

    [Fact]
    public async Task ARequestFinerThanTheWindowLength_IsClampedToTheWindowLength()
    {
        // AB#4246: 96 buckets requested over one day of hourly windows must not yield 15-minute bins, which
        // would drop every window; the bin width is clamped to the window length (1 h), not to a distinct count.
        var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var starts = Enumerable.Range(0, 24).Select(h => from.AddHours(h)).ToList();

        var archive = await CreateArchiveAsync(starts, TimeSpan.FromHours(1));
        var buckets = await CountAsync(archive, from, from.AddHours(24), 96);

        Assert.Equal(24, buckets.Count);
        Assert.All(buckets, b => Assert.Equal(1, b.Count));
    }
}
