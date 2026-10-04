using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Unit tests for the archive-data export (time-slice scan) and import (batched insert) paths added for
/// AB#4230. Drives the public <see cref="CrateDbStreamDataRepository.ExportRowsAsync"/> /
/// <see cref="CrateDbStreamDataRepository.ImportRowsAsync"/> with a faked
/// <see cref="IStreamDataDatabaseClient"/> so no CrateDB instance is needed; the integration of the
/// export SQL against a real Crate fixture is covered separately.
/// </summary>
public class CrateDbStreamDataExportImportTests
{
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> SomeType = new("Test", new CkTypeId("TempSensor"));
    private static readonly string HexRtId = OctoObjectId.GenerateNewId().ToString();

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IStreamDataDatabaseManagementClient _mgmt = A.Fake<IStreamDataDatabaseManagementClient>();
    private readonly ICkCacheService _cache = A.Fake<ICkCacheService>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();
    private readonly IFormulaEngine _formula = A.Fake<IFormulaEngine>();

    private static readonly IOptions<StreamDataConfiguration> Config =
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" });

    private CrateDbStreamDataRepository NewSut() =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance, _cache, _db, _mgmt, Config, "tenant-x", _store, _formula);

    private void StubRaw() =>
        A.CallTo(() => _store.GetAsync(Archive)).Returns(
            new ArchiveSnapshot(Archive, SomeType, CkArchiveStatus.Activated, "voltage-raw",
                new[] { new CkArchiveColumnSpec("Voltage", true, false) }));

    private void StubWindowed() =>
        A.CallTo(() => _store.GetAsync(Archive)).Returns(
            new ArchiveSnapshot(Archive, SomeType, CkArchiveStatus.Disabled, "voltage-window",
                new[] { new CkArchiveColumnSpec("Voltage", true, false) }) { IsTimeRange = true });

    /// <summary>
    /// The export probes <c>information_schema.tables</c> before its first statement (AB#5141). FakeItEasy
    /// answers an unstubbed <c>GetCountAsync</c> with 0 ("no table"), so every test that expects a
    /// scan must declare the table present explicitly.
    /// </summary>
    private void StubTableExists(bool exists) =>
        A.CallTo(() => _db.GetCountAsync("tenant-x", A<string>.That.Contains("information_schema.tables")))
            .Returns(exists ? 1L : 0L);

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Rows(
        params IReadOnlyDictionary<string, object?>[] rows)
    {
        await Task.CompletedTask;
        foreach (var r in rows) yield return r;
    }

    /// <summary>
    /// Stubs the two kinds of statement the export sends: the bounds read (answered with
    /// <paramref name="min"/>, <paramref name="max"/> and <paramref name="count"/>) and the slice
    /// reads (answered by <paramref name="sliceRows"/>). Returns the list every statement is recorded in.
    /// </summary>
    private List<string> StubScan(DateTime? min, DateTime? max, long count,
        Func<string, IReadOnlyDictionary<string, object?>[]>? sliceRows = null)
    {
        var statements = new List<string>();
        // Every slice is counted before it is read; the stub answers with the whole count, spread
        // evenly, so a large archive is cut into several slices and a small one into one.
        A.CallTo(() => _db.GetCountAsync("tenant-x", A<string>.That.StartsWith("SELECT COUNT(*) FROM "), A<CancellationToken>._))
            .ReturnsLazily((string _, string sql, CancellationToken _) =>
            {
                if (min is null || max is null)
                {
                    return 0L;
                }

                var (from, to) = BoundsOf(sql);
                var extent = (max.Value - min.Value).TotalMilliseconds + 1;
                return (long)Math.Round(count * ((to - from).TotalMilliseconds / extent));
            });
        A.CallTo(() => _db.StreamRawRowsAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string sql, CancellationToken _) =>
            {
                statements.Add(sql);
                if (sql.Contains("export_min"))
                {
                    return Rows(new Dictionary<string, object?>
                    {
                        ["export_min"] = min, ["export_max"] = max, ["export_count"] = count,
                    });
                }

                return Rows(sliceRows?.Invoke(sql) ?? []);
            });
        return statements;
    }

    private static readonly System.Text.RegularExpressions.Regex SliceBounds = new(
        "\" >= '(?<from>[^']+)' AND \"[a-z_]+\" (?<op><=?) '(?<to>[^']+)'",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The slice a statement covers, as a half-open interval. The last slice of a scan is written
    /// with an inclusive end (<c>&lt;=</c> the last row); it is returned one millisecond further, so
    /// that every slice reads as <c>[From, To)</c>.
    /// </summary>
    private static (DateTime From, DateTime To) BoundsOf(string sliceSql)
    {
        var m = SliceBounds.Match(sliceSql);
        Assert.True(m.Success, sliceSql);
        DateTime Parse(string v) => DateTime.ParseExact(v, "yyyy-MM-dd HH:mm:ss.fff",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        var to = Parse(m.Groups["to"].Value);
        if (m.Groups["op"].Value == "<=")
        {
            // One millisecond further, unless the row sits at the largest instant there is.
            to = to.Ticks > DateTime.MaxValue.Ticks - TimeSpan.TicksPerMillisecond ? DateTime.MaxValue : to.AddMilliseconds(1);
        }

        return (Parse(m.Groups["from"].Value), to);
    }

    private static async Task<List<IReadOnlyDictionary<string, object?>>> Drain(
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> source)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var r in source)
        {
            rows.Add(r);
        }

        return rows;
    }

    [Fact]
    public async Task Export_WholeArchive_RawShape_ReadsBoundsThenOneOrderedSliceWithoutLimit()
    {
        StubRaw();
        StubTableExists(true);
        var first = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var last = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);
        var statements = StubScan(first, last, count: 3);

        await Drain(NewSut().ExportRowsAsync(Archive, window: null, CancellationToken.None));

        Assert.Equal(2, statements.Count);

        var bounds = statements[0];
        Assert.Contains("MIN(\"timestamp\")", bounds);
        Assert.Contains("MAX(\"timestamp\")", bounds);
        Assert.Contains("COUNT(*)", bounds);
        Assert.DoesNotContain("WHERE", bounds); // whole archive

        var slice = statements[1];
        // The whole primary key of a raw table, so the order is total.
        Assert.EndsWith("ORDER BY \"timestamp\", \"rtid\", \"cktypeid\";", slice);
        Assert.DoesNotContain("window_start", slice);
        // The only slice is the last one: bounded by the last row itself, inclusive.
        Assert.Contains("\"timestamp\" <= '2026-06-02 12:00:00.000'", slice);
        // A page limit is what made the scan slow on a large archive: every page searched the
        // whole rest of the table for its top rows. A slice is bounded by time instead.
        Assert.DoesNotContain("LIMIT", slice);
        // Few rows: the one slice spans the whole extent, the end one millisecond behind the last row.
        Assert.Equal((first, last.AddMilliseconds(1)), BoundsOf(slice));
    }

    [Fact]
    public async Task Export_Windowed_WithTimeWindow_UsesWindowStartOrderAndPredicate()
    {
        StubWindowed();
        StubTableExists(true);
        var first = new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc);
        var last = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
        var statements = StubScan(first, last, count: 10);

        var window = new TimeWindow(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        await Drain(NewSut().ExportRowsAsync(Archive, window, CancellationToken.None));

        Assert.Equal(2, statements.Count);
        // The window restricts the bounds read; the slices then lie inside what it found.
        Assert.Contains("MIN(\"window_start\")", statements[0]);
        Assert.Contains("\"window_start\" >= '2026-06-01 00:00:00.000'", statements[0]);
        Assert.Contains("\"window_start\" < '2026-07-01 00:00:00.000'", statements[0]);

        // A time-range table: window_end completes the primary key; it has no generation column.
        Assert.EndsWith("ORDER BY \"window_start\", \"rtid\", \"cktypeid\", \"window_end\";", statements[1]);
        Assert.Equal((first, last.AddMilliseconds(1)), BoundsOf(statements[1]));
    }

    [Fact]
    public async Task Export_Rollup_OrdersByGenerationLast()
    {
        // A rollup keeps two generations of a window between the pointer flip of a recompute and
        // its sweep. Both are exported; generation in the order keeps their sequence defined.
        A.CallTo(() => _store.GetAsync(Archive)).Returns(
            new ArchiveSnapshot(Archive, SomeType, CkArchiveStatus.Activated, "voltage-hourly",
                new[] { new CkArchiveColumnSpec("Voltage", true, false) })
            {
                RollupAggregations = Array.Empty<CkRollupAggregationSpec>(),
            });
        StubTableExists(true);
        var at = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var statements = StubScan(at, at.AddDays(1), count: 10);

        await Drain(NewSut().ExportRowsAsync(Archive, window: null, CancellationToken.None));

        Assert.EndsWith(
            "ORDER BY \"window_start\", \"rtid\", \"cktypeid\", \"window_end\", \"generation\";", statements[1]);
    }

    [Fact]
    public async Task Export_LastRowAtTheLargestTimestamp_DoesNotOverflow()
    {
        // The exclusive end of the scan lies one millisecond behind the last row. For a row at
        // the largest instant a DateTime can hold that end does not exist, so the last slice is
        // bounded by the row itself.
        StubRaw();
        StubTableExists(true);
        var first = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var largest = new DateTime(DateTime.MaxValue.Ticks - DateTime.MaxValue.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        var statements = StubScan(first, largest, count: 2);

        await Drain(NewSut().ExportRowsAsync(Archive, window: null, CancellationToken.None));

        Assert.Equal(2, statements.Count);
        Assert.Contains("\"timestamp\" >= '2026-06-01 00:00:00.000'", statements[1]);
        Assert.Contains("\"timestamp\" <= '9999-12-31 23:59:59.999'", statements[1]);
    }

    [Fact]
    public async Task Export_PassesItsTokenToTheSliceCounts()
    {
        StubRaw();
        StubTableExists(true);
        var at = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        StubScan(at, at.AddDays(4), count: 1_000_000);
        using var source = new CancellationTokenSource();

        await Drain(NewSut().ExportRowsAsync(Archive, window: null, source.Token));

        A.CallTo(() => _db.GetCountAsync("tenant-x", A<string>.That.StartsWith("SELECT COUNT(*) FROM "), source.Token))
            .MustHaveHappened();
        A.CallTo(() => _db.GetCountAsync("tenant-x", A<string>.That.StartsWith("SELECT COUNT(*) FROM "),
                A<CancellationToken>.That.Not.IsEqualTo(source.Token)))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Export_EmptyTable_ReadsBoundsAndNoSlice()
    {
        StubRaw();
        StubTableExists(true);
        var statements = StubScan(min: null, max: null, count: 0);

        var rows = await Drain(NewSut().ExportRowsAsync(Archive, window: null, CancellationToken.None));

        Assert.Empty(rows);
        Assert.Single(statements); // the bounds read only
        A.CallTo(() => _db.GetCountAsync("tenant-x", A<string>.That.StartsWith("SELECT COUNT(*) FROM "), A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Export_SliceWithoutRows_IsCountedAndNotRead()
    {
        StubRaw();
        StubTableExists(true);
        var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var last = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var statements = StubScan(first, last, count: 1_000_000);
        // Rows only on the first day; the three days behind it are empty.
        A.CallTo(() => _db.GetCountAsync("tenant-x", A<string>.That.StartsWith("SELECT COUNT(*) FROM "), A<CancellationToken>._))
            .ReturnsLazily((string _, string sql, CancellationToken _) => BoundsOf(sql).From < first.AddDays(1) ? 200_000L : 0L);

        await Drain(NewSut().ExportRowsAsync(Archive, window: null, CancellationToken.None));

        var read = statements.Skip(1).Select(BoundsOf).ToList();
        Assert.NotEmpty(read);
        Assert.All(read, slice => Assert.True(slice.From < first.AddDays(1), "an empty slice was read"));
    }

    [Fact]
    public async Task Export_LargeArchive_SlicesPartitionTheExtentInOrderAndYieldEveryRow()
    {
        StubRaw();
        StubTableExists(true);
        var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var last = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        // A million rows over four days: more than one slice. Every slice answers with two rows.
        var statements = StubScan(first, last, count: 1_000_000, sliceRows: sql =>
        {
            var from = BoundsOf(sql).From;
            return
            [
                new Dictionary<string, object?> { [Constants.RtId] = HexRtId, [Constants.Timestamp] = from },
                new Dictionary<string, object?> { [Constants.RtId] = HexRtId, [Constants.Timestamp] = from.AddMilliseconds(1) },
            ];
        });

        var rows = await Drain(NewSut().ExportRowsAsync(Archive, window: null, CancellationToken.None));

        var slices = statements.Skip(1).Select(BoundsOf).ToList();
        Assert.True(slices.Count > 1);
        Assert.Equal(first, slices[0].From);
        Assert.Equal(last.AddMilliseconds(1), slices[^1].To);
        for (var i = 1; i < slices.Count; i++)
        {
            Assert.Equal(slices[i - 1].To, slices[i].From); // no gap, no overlap
            Assert.True(slices[i].To > slices[i].From);
        }

        // Every row of every slice comes out, in the order the slices were read.
        Assert.Equal(slices.Count * 2, rows.Count);
        var stamps = rows.Select(r => (DateTime)r[Constants.Timestamp]!).ToList();
        Assert.Equal(stamps.OrderBy(t => t), stamps);
    }

    [Fact]
    public async Task Export_NoBackingTable_YieldsNothingAndNeverScans()
    {
        // AB#5141: an archive without a provisioned table (never activated) must yield no rows
        // instead of failing the first statement with CrateDB's RelationUnknown (42P01).
        StubRaw();
        StubTableExists(false);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var r in NewSut().ExportRowsAsync(Archive, window: null, CancellationToken.None))
        {
            rows.Add(r);
        }

        Assert.Empty(rows);
        A.CallTo(() => _db.StreamRawRowsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        // The probe targets exactly this archive's table via the catalog, not the data table itself.
        A.CallTo(() => _db.GetCountAsync("tenant-x",
                A<string>.That.Matches(sql =>
                    sql.Contains("information_schema.tables") && sql.Contains($"archive_{Archive}"))))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Export_NoBackingTable_DisabledWindowedWithWindow_YieldsNothing()
    {
        // The blueprint-seeded shape: status Disabled (Archive.Status = 2 straight from the seed),
        // windowed storage, never activated. A windowed export must not even send a statement.
        StubWindowed();
        StubTableExists(false);

        var window = new TimeWindow(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var r in NewSut().ExportRowsAsync(Archive, window, CancellationToken.None))
        {
            rows.Add(r);
        }

        Assert.Empty(rows);
        A.CallTo(() => _db.StreamRawRowsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Export_TableExists_ProbesBeforeTheFirstStatement()
    {
        StubRaw();
        StubTableExists(true);
        var at = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var statements = StubScan(at, at, count: 1,
            sliceRows: _ => [new Dictionary<string, object?> { [Constants.RtId] = HexRtId }]);

        var rows = await Drain(NewSut().ExportRowsAsync(Archive, window: null, CancellationToken.None));

        Assert.Single(rows);
        Assert.Equal(2, statements.Count); // bounds, one slice
        A.CallTo(() => _db.GetCountAsync("tenant-x", A<string>.That.Contains("information_schema.tables")))
            .MustHaveHappenedOnceExactly()
            .Then(A.CallTo(() => _db.StreamRawRowsAsync("tenant-x", A<string>._, A<CancellationToken>._))
                .MustHaveHappened());
    }

    [Fact]
    public async Task Export_MissingSnapshot_Throws()
    {
        A.CallTo(() => _store.GetAsync(Archive)).Returns(Task.FromResult<ArchiveSnapshot?>(null));
        await Assert.ThrowsAsync<ArchiveNotFoundException>(async () =>
        {
            await foreach (var _ in NewSut().ExportRowsAsync(Archive, null, CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task Import_Raw_BatchesIntoInsertDataAsync()
    {
        StubRaw();
        var row = new Dictionary<string, object?>
        {
            [Constants.RtId] = HexRtId,
            [Constants.CkTypeId] = SomeType.ToString(),
            [Constants.Timestamp] = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            ["voltage"] = 230.1,
        };

        await NewSut().ImportRowsAsync(Archive, Rows(row), ArchiveImportMode.InsertOnly, CancellationToken.None);

        A.CallTo(() => _db.InsertDataAsync(
                "tenant-x",
                A<string>.That.Matches(t => t.Contains($"archive_{Archive}")),
                A<IReadOnlyList<string>>.That.Matches(c => c.Contains("voltage")),
                A<IEnumerable<DataPointDto>>.That.Matches(d => d.Count() == 1 && d.First().RtId!.ToString() == HexRtId)))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Import_Raw_LargeRowSet_FlushesInMultipleBatches()
    {
        // AB#4278: a large import must be written in bounded batches (ExportPageSize=5000 per flush),
        // never accumulated into one giant insert. 12 000 rows → 3 flushes (5000 + 5000 + 2000).
        StubRaw();

        var batchSizes = new List<int>();
        A.CallTo(() => _db.InsertDataAsync(
                A<string>._, A<string>._, A<IReadOnlyList<string>>._, A<IEnumerable<DataPointDto>>._))
            // The trailing argument is the archive's opt-in ConflictPrecedence (System.StreamData
            // 1.13.0), null on the import path: an archive-data import restores an operator's
            // snapshot and must write what it is given, not re-decide which write wins.
            .Invokes((string _, string _, IReadOnlyList<string> _, IEnumerable<DataPointDto> d,
                    IReadOnlyList<ArchiveConflictKey>? _) =>
                batchSizes.Add(d.Count()));

        await NewSut().ImportRowsAsync(
            Archive, ManyRawRows(12_000), ArchiveImportMode.InsertOnly, CancellationToken.None);

        Assert.Equal(3, batchSizes.Count);
        Assert.Equal(new[] { 5000, 5000, 2000 }, batchSizes);
        Assert.Equal(12_000, batchSizes.Sum());
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ManyRawRows(int count)
    {
        await Task.CompletedTask;
        var ts = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            yield return new Dictionary<string, object?>
            {
                [Constants.RtId] = HexRtId,
                [Constants.CkTypeId] = SomeType.ToString(),
                [Constants.Timestamp] = ts.AddSeconds(i),
                ["voltage"] = 230.0 + i,
            };
        }
    }

    [Fact]
    public async Task Import_Windowed_UsesTimeRangeInsertPath()
    {
        StubWindowed();
        var row = new Dictionary<string, object?>
        {
            [Constants.RtId] = HexRtId,
            [Constants.CkTypeId] = SomeType.ToString(),
            [Constants.WindowStart] = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            [Constants.WindowEnd] = new DateTime(2026, 6, 1, 1, 0, 0, DateTimeKind.Utc),
            ["voltage"] = 12.5,
        };

        await NewSut().ImportRowsAsync(Archive, Rows(row), ArchiveImportMode.Upsert, CancellationToken.None);

        A.CallTo(() => _db.InsertTimeRangeDataAsync(
                "tenant-x",
                A<string>.That.Matches(t => t.Contains($"archive_{Archive}")),
                A<IReadOnlyList<string>>._,
                A<IEnumerable<TimeRangeDataPointDto>>.That.Matches(d => d.Count() == 1)))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Import_NonHexRtId_ThrowsPerFieldArgumentException()
    {
        StubRaw();
        var row = new Dictionary<string, object?>
        {
            [Constants.RtId] = "not-a-valid-hex-id",
            [Constants.CkTypeId] = SomeType.ToString(),
            [Constants.Timestamp] = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            NewSut().ImportRowsAsync(Archive, Rows(row), ArchiveImportMode.InsertOnly, CancellationToken.None));

        Assert.Contains("rtid", ex.Message);
        Assert.Contains("24-character hex", ex.Message);
        A.CallTo(() => _db.InsertDataAsync(A<string>._, A<string>._, A<IReadOnlyList<string>>._, A<IEnumerable<DataPointDto>>._))
            .MustNotHaveHappened();
    }
}
