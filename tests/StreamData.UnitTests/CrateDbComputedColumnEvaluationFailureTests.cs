using System;
using System.Collections.Generic;
using System.Globalization;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#8331: on prod-1 energyiq an <see cref="OutOfMemoryException"/> during a computed-column backfill was
/// caught and the row stored NULL. An evaluation aborted by an exception must never be persisted as NULL:
/// ingest fails the write, the backfill retries the page once and then fails with the affected range.
/// </summary>
public class CrateDbComputedColumnEvaluationFailureTests
{
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("GridConnection"));
    private static readonly DateTime Ts = new(2026, 7, 11, 15, 20, 0, DateTimeKind.Utc);

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IStreamDataDatabaseManagementClient _mgmt = A.Fake<IStreamDataDatabaseManagementClient>();
    private readonly ICkCacheService _cache = A.Fake<ICkCacheService>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();

    private static readonly IFormulaEngine RealFormula =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static readonly IOptions<StreamDataConfiguration> Config =
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" });

    private CrateDbStreamDataRepository NewSut(IFormulaEngine formula) =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance, _cache, _db, _mgmt, Config, "tenant-x", _store, formula);

    private static CkArchiveColumnSpec Ing(string path) => new(path, Indexed: true, Required: false);

    private static CkArchiveColumnSpec Comp(string name, string formula, ComputedColumnState state) =>
        new(string.Empty, Indexed: true, Required: false)
        {
            Name = name, Formula = formula, ResultType = FormulaResultType.Double, ComputedState = state,
        };

    private static ArchiveSnapshot Snapshot(ComputedColumnState state) =>
        new(Archive, Type, CkArchiveStatus.Activated, "energy",
            new[] { Ing("ActivePower"), Comp("importPower", "if(activepower > 0, activepower, 0)", state) });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Rows(
        params IReadOnlyDictionary<string, object?>[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static Dictionary<string, object?> Row(DateTime ts, string rtId, double? activePower) => new()
    {
        ["timestamp"] = ts, ["rtid"] = rtId, ["cktypeid"] = "GridConnection", ["activepower"] = activePower,
    };

    private List<(string Sql, IReadOnlyList<IReadOnlyList<object?>> Args)> CaptureBulk()
    {
        var executed = new List<(string, IReadOnlyList<IReadOnlyList<object?>>)>();
        A.CallTo(() => _db.ExecuteBulkAsync("tenant-x", A<string>._,
                A<IReadOnlyList<IReadOnlyList<object?>>>._, CrateStatementClass.Maintenance, A<CancellationToken>._))
            .Invokes(call => executed.Add((call.GetArgument<string>(1)!,
                call.GetArgument<IReadOnlyList<IReadOnlyList<object?>>>(2)!)))
            .Returns(Task.CompletedTask);
        return executed;
    }

    /// <summary>Serves the bounds query, the next-row lookup and one-day slice pages from <paramref name="rows"/>.</summary>
    private List<string> ServeSliced(params Dictionary<string, object?>[] rows)
    {
        var queries = new List<string>();
        A.CallTo(() => _db.StreamRawRowsAsync("tenant-x", A<string>._, CrateStatementClass.Maintenance, A<CancellationToken>._))
            .ReturnsLazily((string _, string sql, CrateStatementClass _, CancellationToken _) =>
            {
                queries.Add(sql);
                var times = rows.Select(r => (DateTime)r["timestamp"]!).ToList();
                if (sql.StartsWith("SELECT min(\"timestamp\") AS \"lo\", max(", StringComparison.Ordinal))
                {
                    return Rows(new Dictionary<string, object?> { ["lo"] = times.Min(), ["hi"] = times.Max() });
                }

                if (sql.StartsWith("SELECT min(\"timestamp\") AS \"lo\" FROM", StringComparison.Ordinal))
                {
                    var from = ParseLiteralAfter(sql, "\"timestamp\" >= '");
                    var next = times.Where(t => t >= from).DefaultIfEmpty().Min();
                    return Rows(new Dictionary<string, object?> { ["lo"] = next == default ? null : next });
                }

                var sliceFrom = ParseLiteralAfter(sql, "\"timestamp\" >= '");
                var sliceTo = ParseLiteralAfter(sql, "\"timestamp\" < '");
                var repair = sql.Contains("\"importpower\" IS NULL", StringComparison.Ordinal);
                return Rows(rows.Where(r => (DateTime)r["timestamp"]! >= sliceFrom && (DateTime)r["timestamp"]! < sliceTo)
                    .Where(r => !repair || (r.GetValueOrDefault("importpower") is null && r["activepower"] is not null))
                    .Select(r => (IReadOnlyDictionary<string, object?>)r).ToArray());
            });
        return queries;
    }

    private static DateTime ParseLiteralAfter(string sql, string marker)
    {
        var start = sql.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = sql.IndexOf('\'', start);
        return DateTime.Parse(sql[start..end], CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    // ---------------------------------------------------------------- ingest

    [Fact]
    public async Task Ingest_EvaluationAbortedByAnException_FailsTheWrite_NothingIsStoredAsNull()
    {
        A.CallTo(() => _store.GetAsync(Archive)).Returns(Snapshot(ComputedColumnState.Active));
        var point = new StreamDataPoint
        {
            RtId = OctoObjectId.GenerateNewId(), CkTypeId = Type, Timestamp = Ts,
            Attributes = new Dictionary<string, object?> { ["ActivePower"] = 5.0 },
        };

        await Assert.ThrowsAsync<FormulaEvaluationException>(
            () => NewSut(new AbortingFormulaEngine(abortCalls: int.MaxValue)).InsertAsync(Archive, point));

        A.CallTo(() => _db.InsertDataAsync(A<string>._, A<string>._, A<IReadOnlyList<string>>._,
            A<IEnumerable<DataPointDto>>._, A<IReadOnlyList<ArchiveConflictKey>?>._)).MustNotHaveHappened();
    }

    // ---------------------------------------------------------------- backfill

    [Fact]
    public async Task Backfill_TransientAbort_RetriesThePage_AndWritesTheRealValues()
    {
        ServeSliced(Row(Ts, "a", 5.0), Row(Ts.AddSeconds(1), "b", -3.0));
        var bulk = CaptureBulk();
        var engine = new AbortingFormulaEngine(abortCalls: 1);

        await NewSut(engine).BackfillComputedColumnAsync(Snapshot(ComputedColumnState.Backfilling), "importPower", Ct);

        var (_, args) = Assert.Single(bulk);
        Assert.Equal(new object?[] { 5.0, 0.0 }, args.Select(a => a[0]).ToArray());
        Assert.Equal(1, engine.Aborted);
    }

    [Fact]
    public async Task Backfill_PersistentAbort_FailsWithTheAffectedRange_AndWritesNothing()
    {
        ServeSliced(Row(Ts, "a", 5.0));
        var bulk = CaptureBulk();

        var ex = await Assert.ThrowsAsync<Meshmakers.Octo.Runtime.Contracts.StreamData.StreamDataException>(() =>
            NewSut(new AbortingFormulaEngine(abortCalls: int.MaxValue))
                .BackfillComputedColumnAsync(Snapshot(ComputedColumnState.Backfilling), "importPower", Ct));

        Assert.Empty(bulk);
        Assert.Contains("importpower", ex.Message);
        Assert.Contains("time slice [2026-07-11T15:20:00", ex.Message);
        Assert.Contains("nothing of this page was stored", ex.Message);
        Assert.IsType<FormulaEvaluationException>(ex.InnerException);
    }

    // ---------------------------------------------------------------- repair

    [Fact]
    public async Task Repair_RewritesOnlyNullCellsWithANonNullOperand_InsideTheRange()
    {
        var nullCell = Row(Ts, "a", 5.0);
        nullCell["importpower"] = null;
        var filled = Row(Ts.AddSeconds(1), "b", 7.0);
        filled["importpower"] = 7.0;
        var noOperand = Row(Ts.AddSeconds(2), "c", null);
        noOperand["importpower"] = null;
        var outsideRange = Row(Ts.AddDays(3), "d", 9.0);
        outsideRange["importpower"] = null;
        var queries = ServeSliced(nullCell, filled, noOperand, outsideRange);
        var bulk = CaptureBulk();
        var reported = new List<ComputedColumnBackfillProgress>();

        await NewSut(RealFormula).RepairComputedColumnAsync(Snapshot(ComputedColumnState.Active), "importPower",
            Ts.Date, Ts.Date.AddDays(1), new SyncProgress(reported.Add), Ct);

        var (sql, args) = Assert.Single(bulk);
        Assert.Contains("SET \"importpower\" = $1", sql);
        var written = Assert.Single(args);
        Assert.Equal(5.0, written[0]);
        Assert.Equal("a", written[2]);
        var pages = queries.Where(q => q.Contains("ORDER BY", StringComparison.Ordinal)).ToList();
        Assert.All(pages, q => Assert.Contains("(\"importpower\" IS NULL AND (\"activepower\" IS NOT NULL))", q));
        Assert.DoesNotContain(pages, q => q.Contains("2026-07-14", StringComparison.Ordinal));
        Assert.Equal(Ts.Date.AddDays(1), reported[^1].CompletedThrough);
    }

    [Fact]
    public async Task Repair_OfAColumnThatDoesNotExist_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSut(RealFormula).RepairComputedColumnAsync(Snapshot(ComputedColumnState.Active), "nope",
                DateTime.UnixEpoch, Ts, null, Ct));
    }

    // ---------------------------------------------------------------- large synthetic archive

    /// <summary>
    /// The acceptance test of AB#8331 at the repository level: a sliced backfill over a synthetic archive of
    /// 2,000,000 rows (one row per second over ~23 days, the size of the prod-1 GridConnectionArchive /
    /// BatteryStorageArchive) with the real formula engine. The fake database generates every page on the fly,
    /// so the measured memory is the backfill's own. Before the fix every row allocated ~200 KiB in the formula
    /// engine alone (~400 GB for this run); now the live managed heap stays flat (sampled after a full collection every 100,000 rows).
    /// </summary>
    [Fact]
    public async Task Backfill_TwoMillionRows_RunsWithBoundedMemory()
    {
        const int rowCount = 2_000_000;
        var first = new DateTime(2026, 7, 7, 13, 40, 0, DateTimeKind.Utc);
        var last = first.AddSeconds(rowCount - 1);
        A.CallTo(() => _db.StreamRawRowsAsync("tenant-x", A<string>._, CrateStatementClass.Maintenance, A<CancellationToken>._))
            .ReturnsLazily((string _, string sql, CrateStatementClass _, CancellationToken _) =>
                GeneratedRows(sql, first, last));
        long written = 0;
        long peakHeap = 0;
        var bulkCalls = 0;
        A.CallTo(() => _db.ExecuteBulkAsync("tenant-x", A<string>._,
                A<IReadOnlyList<IReadOnlyList<object?>>>._, CrateStatementClass.Maintenance, A<CancellationToken>._))
            .Invokes(call =>
            {
                written += call.GetArgument<IReadOnlyList<IReadOnlyList<object?>>>(2)!.Count;
                // The fake records every call with its arguments; drop them, or the test measures the fake.
                Fake.ClearRecordedCalls(_db);
                if (++bulkCalls % 100 == 0)
                {
                    // Live memory after a full collection, sampled every 100 pages (100,000 rows).
                    peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(true));
                }
            })
            .Returns(Task.CompletedTask);

        GC.Collect();
        var heapBefore = GC.GetTotalMemory(true);
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var started = DateTime.UtcNow;

        await NewSut(RealFormula).BackfillComputedColumnAsync(Snapshot(ComputedColumnState.Backfilling), "importPower", Ct);

        var allocatedPerRow = (GC.GetTotalAllocatedBytes(true) - allocatedBefore) / (double)rowCount;
        var peakGrowthMiB = (peakHeap - heapBefore) / 1024.0 / 1024.0;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{written:N0} rows in {(DateTime.UtcNow - started).TotalSeconds:F1} s, {allocatedPerRow:F0} B allocated per row, " +
            $"peak live managed heap +{peakGrowthMiB:F0} MiB");

        Assert.Equal(rowCount, written);
        Assert.True(allocatedPerRow < 32 * 1024, $"allocated {allocatedPerRow:F0} B per row");
        Assert.True(peakGrowthMiB < 64, $"peak managed heap grew by {peakGrowthMiB:F0} MiB");
    }

    /// <summary>One row per second in [first, last]; answers the bounds, next-row and keyset page queries.</summary>
    private static IAsyncEnumerable<IReadOnlyDictionary<string, object?>> GeneratedRows(string sql, DateTime first, DateTime last)
    {
        if (sql.StartsWith("SELECT min(\"timestamp\") AS \"lo\", max(", StringComparison.Ordinal))
        {
            return Rows(new Dictionary<string, object?> { ["lo"] = first, ["hi"] = last });
        }

        if (sql.StartsWith("SELECT min(\"timestamp\") AS \"lo\" FROM", StringComparison.Ordinal))
        {
            var from = ParseLiteralAfter(sql, "\"timestamp\" >= '");
            return Rows(new Dictionary<string, object?> { ["lo"] = from <= last ? (from < first ? first : from) : null });
        }

        var sliceFrom = ParseLiteralAfter(sql, "\"timestamp\" >= '");
        var sliceTo = ParseLiteralAfter(sql, "\"timestamp\" < '");
        var start = sql.Contains("(\"timestamp\" > '", StringComparison.Ordinal)
            ? ParseLiteralAfter(sql, "(\"timestamp\" > '").AddSeconds(1)
            : sliceFrom;
        if (start < first) start = first;
        return Page(start, sliceTo < last.AddSeconds(1) ? sliceTo : last.AddSeconds(1));
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Page(DateTime start, DateTime endExclusive)
    {
        var ts = start;
        for (var i = 0; i < 1000 && ts < endExclusive; i++, ts = ts.AddSeconds(1))
        {
            yield return new Dictionary<string, object?>
            {
                ["timestamp"] = ts, ["rtid"] = "6a0e000000000000000a0007", ["cktypeid"] = "GridConnection",
                ["activepower"] = (ts.Ticks / TimeSpan.TicksPerSecond % 200) - 100.0,
            };
        }

        await Task.CompletedTask;
    }

    private sealed class SyncProgress(Action<ComputedColumnBackfillProgress> report) : IProgress<ComputedColumnBackfillProgress>
    {
        public void Report(ComputedColumnBackfillProgress value) => report(value);
    }

    /// <summary>The real engine, but the first <c>abortCalls</c> evaluations are aborted like an OOM inside mXparser.</summary>
    private sealed class AbortingFormulaEngine(int abortCalls) : IFormulaEngine
    {
        public int Aborted { get; private set; }

        public string NormalizeTernary(string expression) => RealFormula.NormalizeTernary(expression);

        public FormulaSyntaxResult Validate(string expression, IReadOnlyDictionary<string, double> arguments) =>
            RealFormula.Validate(expression, arguments);

        public FormulaSyntaxResult CheckSyntax(string expression, IReadOnlyCollection<string> argumentNames) =>
            RealFormula.CheckSyntax(expression, argumentNames);

        public double EvaluateRaw(string expression, IReadOnlyDictionary<string, double> arguments)
        {
            if (Aborted < abortCalls)
            {
                Aborted++;
                throw new FormulaEvaluationException(expression, "aborted by OutOfMemoryException");
            }

            return RealFormula.EvaluateRaw(expression, arguments);
        }

        public object? Evaluate(string expression, IReadOnlyDictionary<string, double> arguments, FormulaResultType resultType)
        {
            if (Aborted < abortCalls)
            {
                Aborted++;
                throw new FormulaEvaluationException(expression, "aborted by OutOfMemoryException");
            }

            return RealFormula.Evaluate(expression, arguments, resultType);
        }
    }
}
