using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Reconcile;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Reconcile;

/// <summary>
/// AB#6968: the four checks of the rollup validator, one test per check, against a scripted database that records every
/// statement. The validator is read only: counts and MIN/MAX, plus the small pointer table.
/// </summary>
public class RollupConsistencyValidatorTests
{
    private const string Tenant = "acmecorp";
    private const long HourMs = 3_600_000;
    private static readonly long T0 = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Sensor"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly OctoObjectId _rollup = OctoObjectId.GenerateNewId();
    private readonly OctoObjectId _source = OctoObjectId.GenerateNewId();
    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IArchiveRuntimeStore _archives = A.Fake<IArchiveRuntimeStore>();
    private readonly IRollupArchiveRuntimeStore _rollups = A.Fake<IRollupArchiveRuntimeStore>();

    // what the database "holds"
    private bool _liveExists = true;
    private bool _genMapExists = true;
    private readonly List<GenerationRange> _pointers = [];
    private readonly Dictionary<long, (long Rows, long First, long Last)> _generations = new();
    private DateTime? _maxWindowEnd;
    private long _sourceRows;
    private Func<string, long?> _count = _ => null;
    private readonly List<string> _statements = [];

    private static DateTime At(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    public RollupConsistencyValidatorTests()
    {
        A.CallTo(() => _db.GetCountAsync(A<string>._, A<string>._, A<CrateStatementClass>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string sql, CrateStatementClass _, CancellationToken _) =>
            {
                _statements.Add(sql);
                return Task.FromResult(Count(sql));
            });
        A.CallTo(() => _db.RefreshArchiveTableAsync(A<string>._, A<string>._, A<CrateStatementClass>._, A<CancellationToken>._))
            .Returns(Task.CompletedTask);
        A.CallTo(() => _db.StreamRawRowsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string sql, CancellationToken _) =>
            {
                _statements.Add(sql);
                return Rows(sql);
            });
        A.CallTo(() => _archives.GetAsync(_source)).Returns(new ArchiveSnapshot(_source, Type, CkArchiveStatus.Activated, "raw",
            new List<CkArchiveColumnSpec>()));
    }

    private long Count(string sql)
    {
        if (_count(sql) is { } scripted)
        {
            return scripted;
        }

        if (sql.Contains("information_schema.tables", StringComparison.Ordinal))
        {
            return sql.Contains("__genmap", StringComparison.Ordinal) ? (_genMapExists ? 1 : 0) : (_liveExists ? 1 : 0);
        }

        if (sql.Contains("\"generation\" = ", StringComparison.Ordinal) && sql.Contains("FROM \"", StringComparison.Ordinal))
        {
            var g = long.Parse(System.Text.RegularExpressions.Regex.Match(sql, "\"generation\" = (\\d+)").Groups[1].Value);
            if (sql.Contains("NOT (", StringComparison.Ordinal))
            {
                // an orphan count: scripted by the test through _count
                return 0;
            }

            return _generations.TryGetValue(g, out var s) ? s.Rows : 0;
        }

        if (sql.Contains(_source.ToString(), StringComparison.Ordinal))
        {
            return _sourceRows;
        }

        throw new NotSupportedException($"Unexpected count: {sql}");
    }

    private async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Rows(string sql)
    {
        await Task.CompletedTask;
        if (sql.Contains("\"range_start\"", StringComparison.Ordinal))
        {
            foreach (var p in _pointers)
            {
                yield return new Dictionary<string, object?>
                {
                    ["range_start"] = p.StartMs, ["range_end"] = p.EndMs, ["rtid_scope"] = p.Scope, ["generation"] = p.Generation
                };
            }
        }
        else if (sql.Contains("GROUP BY", StringComparison.Ordinal))
        {
            foreach (var (g, s) in _generations.Where(x => x.Key > 0))
            {
                yield return new Dictionary<string, object?> { ["g"] = g, ["c"] = s.Rows, ["lo"] = At(s.First), ["hi"] = At(s.Last) };
            }
        }
        else if (sql.Contains("max_end", StringComparison.Ordinal))
        {
            yield return new Dictionary<string, object?> { ["max_end"] = _maxWindowEnd };
        }
    }

    private RollupArchiveSnapshot Rollup(DateTime? watermark = null, bool recomputeInProgress = false,
        CkArchiveStatus status = CkArchiveStatus.Activated, RollupSourceReference[]? sources = null) => new(
        _rollup, Type, status, "hourly", sources ?? [new RollupSourceReference(_source)], TimeSpan.FromHours(1), TimeSpan.Zero,
        watermark, new[] { new CkRollupAggregationSpec("Value", CkRollupFunction.Sum, null) }, null)
    {
        RecomputeInProgress = recomputeInProgress
    };

    private async Task<IReadOnlyList<RollupConsistencyFinding>> ValidateAsync(RollupArchiveSnapshot rollup)
    {
        A.CallTo(() => _rollups.EnumerateAsync()).Returns(Enumerate(rollup));
        var report = await new RollupConsistencyValidator(
            Tenant, _db, _archives, _rollups, NullLogger.Instance, TimeProvider.System).ValidateAsync(null, Ct);
        return report.Rollups.Single().Findings;
    }

    private static async IAsyncEnumerable<RollupArchiveSnapshot> Enumerate(params RollupArchiveSnapshot[] snapshots)
    {
        await Task.CompletedTask;
        foreach (var s in snapshots)
        {
            yield return s;
        }
    }

    // ---- consistent --------------------------------------------------------------------------------------

    [Fact]
    public async Task ConsistentRollup_HasNoFindings_AndTheValidatorOnlyReadsCountsMinMaxAndThePointers()
    {
        _pointers.Add(new GenerationRange(T0, T0 + 24 * HourMs, "", 1));
        _generations[1] = (24, T0, T0 + 23 * HourMs);
        _maxWindowEnd = At(T0 + 48 * HourMs);
        _sourceRows = 10;

        var findings = await ValidateAsync(Rollup(watermark: At(T0 + 48 * HourMs)));

        Assert.Empty(findings);
        Assert.NotEmpty(_statements);
        Assert.All(_statements, sql =>
        {
            Assert.StartsWith("SELECT", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("SELECT *", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("DELETE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("INSERT", sql, StringComparison.Ordinal);
        });
    }

    private async Task<RollupConsistencyEntry> ValidateEntryAsync(RollupArchiveSnapshot rollup)
    {
        A.CallTo(() => _rollups.EnumerateAsync()).Returns(Enumerate(rollup));
        var report = await new RollupConsistencyValidator(
            Tenant, _db, _archives, _rollups, NullLogger.Instance, TimeProvider.System).ValidateAsync(null, Ct);
        return report.Rollups.Single();
    }

    // ---- not checked: nothing found is not "consistent" -----------------------------------------------------------

    [Fact]
    public async Task ARollupThatWasNeverActivated_IsNotChecked_AndIsNotOk()
    {
        _liveExists = false;

        var entry = await ValidateEntryAsync(Rollup(status: CkArchiveStatus.Created));

        Assert.Empty(entry.Findings);
        Assert.False(entry.IsOk);
        Assert.Equal(RollupNotCheckedCause.NeverActivated, entry.NotChecked!.Cause);
        Assert.Contains("never activated", entry.NotChecked.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CkArchiveStatus.Disabled)]
    [InlineData(CkArchiveStatus.Failed)]
    [InlineData(CkArchiveStatus.Activated)]
    public async Task ARollupWithoutItsTable_IsNotChecked_WhateverItsStatus(CkArchiveStatus status)
    {
        _liveExists = false;

        var entry = await ValidateEntryAsync(Rollup(status: status));

        Assert.False(entry.IsOk);
        Assert.Equal(RollupNotCheckedCause.NoLiveTable, entry.NotChecked!.Cause);
        Assert.Contains(status.ToString(), entry.NotChecked.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReportIsNotOk_WhileAnyRollupWasNotChecked_EvenWhenTheOthersAreConsistent()
    {
        _liveExists = true;
        _pointers.Add(new GenerationRange(T0, T0 + 24 * HourMs, "", 1));
        _generations[1] = (24, T0, T0 + 23 * HourMs);
        _maxWindowEnd = At(T0 + 48 * HourMs);
        _sourceRows = 10;
        var consistent = Rollup(watermark: At(T0 + 48 * HourMs));
        var missing = new RollupArchiveSnapshot(OctoObjectId.GenerateNewId(), Type, CkArchiveStatus.Disabled, "hourly",
            [new RollupSourceReference(_source)], TimeSpan.FromHours(1), TimeSpan.Zero, null,
            new[] { new CkRollupAggregationSpec("Value", CkRollupFunction.Sum, null) }, null);
        // The scripted database answers "table exists" for every table, so the missing rollup is told apart by its id.
        _count = sql => sql.Contains("information_schema.tables", StringComparison.Ordinal) &&
                        sql.Contains(missing.RtId.ToString(), StringComparison.Ordinal) ? 0 : null;
        A.CallTo(() => _rollups.EnumerateAsync()).Returns(Enumerate(consistent, missing));

        var report = await new RollupConsistencyValidator(
            Tenant, _db, _archives, _rollups, NullLogger.Instance, TimeProvider.System).ValidateAsync(null, Ct);

        Assert.Equal(2, report.Rollups.Count);
        Assert.True(report.Rollups[0].IsOk);
        Assert.True(report.Rollups[1].IsNotChecked);
        Assert.False(report.IsOk);
        Assert.True(report.HasNotChecked);
    }

    [Fact]
    public async Task ASourceThatDoesNotExist_MakesTheRollupNotChecked_ButTheOtherChecksStillRun()
    {
        A.CallTo(() => _archives.GetAsync(_source)).Returns((ArchiveSnapshot?)null);
        _genMapExists = false;

        var entry = await ValidateEntryAsync(Rollup());

        Assert.Equal(RollupNotCheckedCause.SourceMissing, entry.NotChecked!.Cause);
        Assert.Contains(_source.ToString(), entry.NotChecked.Reason, StringComparison.Ordinal);
        Assert.Contains(entry.Findings, f => f.Check == RollupConsistencyCheck.MissingGenerationMap); // still reported
        Assert.False(entry.IsOk);
    }

    [Fact]
    public async Task AnActivatedSourceWithoutItsTable_MakesTheRollupNotChecked()
    {
        _count = sql => sql.Contains("information_schema.tables", StringComparison.Ordinal) &&
                        sql.Contains(_source.ToString(), StringComparison.Ordinal) ? 0 : null;
        _pointers.Add(new GenerationRange(T0, T0 + 24 * HourMs, "", 1));
        _generations[1] = (24, T0, T0 + 23 * HourMs);

        var entry = await ValidateEntryAsync(Rollup());

        Assert.Equal(RollupNotCheckedCause.SourceMissing, entry.NotChecked!.Cause);
        Assert.Empty(entry.Findings);
        Assert.False(entry.IsOk);
    }

    [Fact]
    public async Task ASourceThatWasNeverActivated_HasNoTableByDesign_AndDoesNotBlock()
    {
        A.CallTo(() => _archives.GetAsync(_source)).Returns(new ArchiveSnapshot(_source, Type, CkArchiveStatus.Created, "raw",
            new List<CkArchiveColumnSpec>()));
        _count = sql => sql.Contains("information_schema.tables", StringComparison.Ordinal) &&
                        sql.Contains(_source.ToString(), StringComparison.Ordinal) ? 0 : null;
        _pointers.Add(new GenerationRange(T0, T0 + 24 * HourMs, "", 1));
        _generations[1] = (24, T0, T0 + 23 * HourMs);

        var entry = await ValidateEntryAsync(Rollup());

        Assert.Null(entry.NotChecked);
        Assert.True(entry.IsOk);
    }

    // ---- (a) pointer without rows ------------------------------------------------------------------------

    [Fact]
    public async Task A_APointerEntryWhoseGenerationHasNoRows_IsReported_WithItsRangeAndGeneration()
    {
        _pointers.Add(new GenerationRange(T0, T0 + 24 * HourMs, "", 5)); // no rows of generation 5

        var finding = Assert.Single(await ValidateAsync(Rollup()));

        Assert.Equal(RollupConsistencyCheck.PointerWithoutRows, finding.Check);
        Assert.Equal(5, finding.Generation);
        Assert.Equal(At(T0), finding.RangeFromUtc);
        Assert.Equal(At(T0 + 24 * HourMs), finding.RangeToUtc);
        Assert.Contains(_rollup.ToString(), finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_OnlyThePartThatNoHigherEntryOutranks_IsChecked_SoTheHoleInTheMiddleIsFound()
    {
        // entry 3 (no rows) over the whole day, entry 7 with rows over hours 8..12: the hole is hours 0..8 and 12..24
        _pointers.Add(new GenerationRange(T0, T0 + 24 * HourMs, "", 3));
        _pointers.Add(new GenerationRange(T0 + 8 * HourMs, T0 + 12 * HourMs, "", 7));
        _generations[7] = (4, T0 + 8 * HourMs, T0 + 11 * HourMs);

        var findings = await ValidateAsync(Rollup());

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(RollupConsistencyCheck.PointerWithoutRows, f.Check));
        Assert.Contains(findings, f => f.RangeFromUtc == At(T0) && f.RangeToUtc == At(T0 + 8 * HourMs));
        Assert.Contains(findings, f => f.RangeFromUtc == At(T0 + 12 * HourMs) && f.RangeToUtc == At(T0 + 24 * HourMs));
    }

    [Fact]
    public void A_GovernedRanges_AreTheEntryMinusTheRangesOfHigherGenerations()
    {
        var entry = new GenerationRange(0, 100, "", 3);
        var higher = new GenerationRange(20, 40, "", 4);
        var higherToo = new GenerationRange(30, 60, "", 9);
        var lower = new GenerationRange(70, 80, "", 2);
        var scopedHigher = new GenerationRange(60, 100, "rt1", 8);
        var all = new[] { entry, higher, higherToo, lower, scopedHigher };

        // a scoped entry does not outrank an unscoped one for the other entities: 60..100 stays governed
        Assert.Equal([(0L, 20L), (60L, 100L)], RollupConsistencySql.GovernedRanges(entry, all));
        Assert.Equal([(70L, 80L)], RollupConsistencySql.GovernedRanges(lower, [lower]));
        Assert.Empty(RollupConsistencySql.GovernedRanges(lower, [lower, new GenerationRange(0, 100, "", 5)]));
        // the same scope outranks
        var scoped = new GenerationRange(0, 100, "rt1", 3);
        Assert.Equal([(0L, 60L)], RollupConsistencySql.GovernedRanges(scoped, [scoped, scopedHigher]));
    }

    // ---- (b) orphans above the pointer ---------------------------------------------------------------------

    [Fact]
    public async Task B_RowsOfAGenerationNoEntryCovers_AreReportedAsOrphans_WithTheirCount()
    {
        _pointers.Add(new GenerationRange(T0, T0 + 24 * HourMs, "", 2));
        _generations[2] = (24, T0, T0 + 23 * HourMs);
        _generations[3] = (10, T0 + 30 * HourMs, T0 + 40 * HourMs); // above every pointer, outside the entry
        _count = sql => sql.Contains("\"generation\" = 3", StringComparison.Ordinal) ? 10 : null;

        var finding = Assert.Single(await ValidateAsync(Rollup()));

        Assert.Equal(RollupConsistencyCheck.OrphansAbovePointer, finding.Check);
        Assert.Equal(3, finding.Generation);
        Assert.Equal(10, finding.Rows);
        // no entry of generation 3 or higher exists: every row of the generation counts
        Assert.Contains(_statements, s => s.StartsWith("SELECT COUNT(*)", StringComparison.Ordinal)
                                          && s.EndsWith("WHERE \"generation\" = 3;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task B_RowsOutsideTheEntryThatCoversTheGeneration_AreCountedWithTheComplementOfTheCoveredRanges()
    {
        _pointers.Add(new GenerationRange(T0, T0 + 24 * HourMs, "", 3));
        _generations[3] = (30, T0, T0 + 30 * HourMs); // hours 24..30 lie outside the entry
        _count = sql => sql.Contains("\"generation\" = 3 AND NOT (", StringComparison.Ordinal) ? 6 : null;

        var finding = Assert.Single(await ValidateAsync(Rollup()));

        Assert.Equal(6, finding.Rows);
        Assert.Contains(_statements, s => s.Contains(
            $"NOT ((\"window_start\" >= {T0} AND \"window_start\" < {T0 + 24 * HourMs}))", StringComparison.Ordinal));
    }

    [Fact]
    public async Task B_OrphansAreNotReported_WhileARecomputeRuns()
    {
        _pointers.Add(new GenerationRange(T0, T0 + 24 * HourMs, "", 2));
        _generations[2] = (24, T0, T0 + 23 * HourMs);
        _generations[3] = (10, T0 + 30 * HourMs, T0 + 40 * HourMs); // the uncommitted copy of the running recompute
        _count = sql => sql.Contains("NOT (", StringComparison.Ordinal) ? 10 : null;

        var findings = await ValidateAsync(Rollup(recomputeInProgress: true));

        Assert.Empty(findings);
        Assert.DoesNotContain(_statements, s => s.Contains("NOT (", StringComparison.Ordinal));
    }

    [Fact]
    public async Task B_RowsInsideAnEntryOfAHigherOrEqualGeneration_AreNotOrphans_AndCostNoStatement()
    {
        _pointers.Add(new GenerationRange(T0, T0 + 48 * HourMs, "", 4));
        _generations[2] = (24, T0, T0 + 23 * HourMs); // superseded by generation 4, not an orphan
        _generations[4] = (48, T0, T0 + 47 * HourMs);

        var findings = await ValidateAsync(Rollup());

        Assert.Empty(findings);
        Assert.DoesNotContain(_statements, s => s.Contains("NOT (", StringComparison.Ordinal));
    }

    [Fact]
    public async Task B_WithoutAnyPointer_EveryRecomputedRowIsAnOrphan()
    {
        _generations[2] = (24, T0, T0 + 23 * HourMs);
        _count = sql => sql.Contains("\"generation\" = 2", StringComparison.Ordinal) && !sql.Contains("information_schema", StringComparison.Ordinal) ? 24 : null;

        var finding = Assert.Single(await ValidateAsync(Rollup()));

        Assert.Equal(RollupConsistencyCheck.OrphansAbovePointer, finding.Check);
        Assert.Equal(24, finding.Rows);
    }

    // ---- (c) watermark ahead of the rows ------------------------------------------------------------------

    [Fact]
    public async Task C_AWatermarkNewerThanTheLastRollupRow_WithSourceRowsBetween_IsReported_WithTheGap()
    {
        _maxWindowEnd = At(T0 + 10 * HourMs);
        _sourceRows = 120;

        var finding = Assert.Single(await ValidateAsync(Rollup(watermark: At(T0 + 16 * HourMs))));

        Assert.Equal(RollupConsistencyCheck.WatermarkAheadOfRows, finding.Check);
        Assert.Equal(TimeSpan.FromHours(6), finding.Gap);
        Assert.Equal(120, finding.Rows);
        Assert.Equal(At(T0 + 10 * HourMs), finding.RangeFromUtc);
        Assert.Equal(At(T0 + 16 * HourMs), finding.RangeToUtc);
        Assert.Contains(_rollup.ToString(), finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task C_AQuietSource_IsNotAFinding()
    {
        _maxWindowEnd = At(T0 + 10 * HourMs);
        _sourceRows = 0; // nothing to aggregate between the last row and the watermark

        Assert.Empty(await ValidateAsync(Rollup(watermark: At(T0 + 16 * HourMs))));
    }

    [Fact]
    public async Task C_WatermarkAtOrBehindTheLastRow_IsNotAFinding_AndNoSourceIsQueried()
    {
        _maxWindowEnd = At(T0 + 16 * HourMs);
        _sourceRows = 99;

        Assert.Empty(await ValidateAsync(Rollup(watermark: At(T0 + 16 * HourMs))));
        // no row of the source is counted (only the catalogue is asked whether its table exists)
        Assert.DoesNotContain(_statements, s =>
            s.Contains(_source.ToString(), StringComparison.Ordinal) && !s.Contains("information_schema.tables", StringComparison.Ordinal));
    }

    [Fact]
    public async Task C_AnEmptyRollupWithAWatermark_AndSourceRows_IsReported()
    {
        _maxWindowEnd = null;
        _sourceRows = 5;

        var finding = Assert.Single(await ValidateAsync(Rollup(watermark: At(T0))));

        Assert.Equal(RollupConsistencyCheck.WatermarkAheadOfRows, finding.Check);
        Assert.Null(finding.Gap);
        Assert.Contains("no rows", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task C_OnlyTheValiditySpanOfASource_IsCounted()
    {
        _maxWindowEnd = At(T0);
        _sourceRows = 7;
        var span = new RollupSourceReference(_source, At(T0 + 4 * HourMs), At(T0 + 6 * HourMs));

        await ValidateAsync(Rollup(watermark: At(T0 + 16 * HourMs), sources: [span]));

        Assert.Contains(_statements, s => s.Contains($">= {T0 + 4 * HourMs} AND", StringComparison.Ordinal)
                                          && s.Contains($"< {T0 + 6 * HourMs}", StringComparison.Ordinal));
    }

    // ---- (d) rollup without generation map ---------------------------------------------------------------

    [Fact]
    public async Task D_ARollupTableWithoutItsGenerationMap_IsReported_AndThePointerTableIsNotRead()
    {
        _genMapExists = false;

        var finding = Assert.Single(await ValidateAsync(Rollup()));

        Assert.Equal(RollupConsistencyCheck.MissingGenerationMap, finding.Check);
        Assert.Contains("__genmap", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_statements, s => s.Contains("\"range_start\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyTheRequestedRollups_AreChecked()
    {
        A.CallTo(() => _rollups.EnumerateAsync()).Returns(Enumerate(Rollup()));

        var report = await new RollupConsistencyValidator(Tenant, _db, _archives, _rollups, NullLogger.Instance, TimeProvider.System)
            .ValidateAsync([OctoObjectId.GenerateNewId()], Ct);

        Assert.Empty(report.Rollups);
        Assert.True(report.IsOk);
    }
}
