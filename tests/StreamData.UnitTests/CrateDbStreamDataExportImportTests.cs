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
/// Unit tests for the archive-data export (keyset scan) and import (batched insert) paths added for
/// AB#4230. Drives the public <see cref="CrateDbStreamDataRepository.ExportRowsAsync"/> /
/// <see cref="CrateDbStreamDataRepository.ImportRowsAsync"/> with a faked
/// <see cref="IStreamDataDatabaseClient"/> so no CrateDB instance is needed; the integration of the
/// keyset SQL against a real Crate fixture is covered separately.
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

    [Fact]
    public async Task Export_WholeArchive_RawShape_UsesTimestampOrderAndNoWindowPredicate()
    {
        StubRaw();
        string? capturedSql = null;
        A.CallTo(() => _db.StreamRawRowsAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string sql, CancellationToken _) =>
            {
                capturedSql = sql;
                return Empty();
            });

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var r in NewSut().ExportRowsAsync(Archive, window: null, CancellationToken.None))
        {
            rows.Add(r);
        }

        Assert.Empty(rows);
        Assert.NotNull(capturedSql);
        Assert.Contains("ORDER BY \"timestamp\", \"rtid\"", capturedSql);
        Assert.DoesNotContain("window_start", capturedSql);
        Assert.DoesNotContain("WHERE", capturedSql); // whole archive, no cursor on first page
        Assert.Contains("LIMIT 5000", capturedSql);
    }

    [Fact]
    public async Task Export_Windowed_WithTimeWindow_UsesWindowStartOrderAndPredicate()
    {
        StubWindowed();
        string? capturedSql = null;
        A.CallTo(() => _db.StreamRawRowsAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string sql, CancellationToken _) =>
            {
                capturedSql = sql;
                return Empty();
            });

        var window = new TimeWindow(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        await foreach (var _ in NewSut().ExportRowsAsync(Archive, window, CancellationToken.None)) { }

        Assert.NotNull(capturedSql);
        Assert.Contains("ORDER BY \"window_start\", \"rtid\", \"cktypeid\"", capturedSql);
        Assert.Contains("\"window_start\" >= '2026-06-01 00:00:00.000'", capturedSql);
        Assert.Contains("\"window_start\" < '2026-07-01 00:00:00.000'", capturedSql);
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
            .Invokes((string _, string _, IReadOnlyList<string> _, IEnumerable<DataPointDto> d) =>
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
