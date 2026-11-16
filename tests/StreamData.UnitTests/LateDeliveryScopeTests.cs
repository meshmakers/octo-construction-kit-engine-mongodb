using System;
using System.Collections.Generic;
using System.Linq;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#9016: the storage side of the late-delivery recompute - the detector records the series of a late delivery, the
/// MongoDB records carry them, and the SQL of the in-place refresh restricts and places the rows.
/// </summary>
public class LateDeliveryScopeTests
{
    private static readonly OctoObjectId M1 = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId M2 = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId M3 = OctoObjectId.GenerateNewId();
    private static readonly DateTime Watermark = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DetectedAt = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime Late(int hour) => Watermark.AddDays(-2).AddHours(hour);

    // ---- Detector ----------------------------------------------------------------------------

    [Fact]
    public void Detector_RecordsTheDistinctRtIdsOfTheLateRows()
    {
        var ok = RetroactiveWriteDetector.TryBuildDirtyWindow(
            Watermark, new[] { (Late(8), M2), (Late(9), M1), (Late(10), M2), (Watermark.AddHours(1), M3) },
            RecomputeChangeSource.Pipeline, DetectedAt, maxRetroactiveReach: null, maxScopedRtIds: 200,
            out var window, out _);

        Assert.True(ok);
        // M3's row is a forward append: it does not make the window dirty, so it is not part of the scope.
        Assert.Equal(RtIdSet.Of(new[] { M1, M2 }), window.RtIds);
        Assert.Equal(Late(8), window.WindowStart);
    }

    [Fact]
    public void Detector_LeavesTheRowsBeyondTheReachCapOutOfTheScope()
    {
        var ok = RetroactiveWriteDetector.TryBuildDirtyWindow(
            Watermark, new[] { (Watermark.AddDays(-30), M3), (Late(8), M1) },
            RecomputeChangeSource.Pipeline, DetectedAt, maxRetroactiveReach: TimeSpan.FromDays(7), maxScopedRtIds: 200,
            out var window, out var capped);

        Assert.True(ok);
        Assert.True(capped);
        Assert.Equal(RtIdSet.Of(new[] { M1 }), window.RtIds);
    }

    [Fact]
    public void Detector_AboveTheThreshold_RecordsEverySeries()
    {
        var ok = RetroactiveWriteDetector.TryBuildDirtyWindow(
            Watermark, new[] { (Late(8), M1), (Late(8), M2), (Late(8), M3) },
            RecomputeChangeSource.Pipeline, DetectedAt, maxRetroactiveReach: null, maxScopedRtIds: 2,
            out var window, out _);

        Assert.True(ok);
        Assert.Null(window.RtIds);
    }

    [Fact]
    public void Detector_ThresholdZero_And_TheTimestampOverload_RecordEverySeries()
    {
        Assert.True(RetroactiveWriteDetector.TryBuildDirtyWindow(
            Watermark, new[] { (Late(8), M1) }, RecomputeChangeSource.Pipeline, DetectedAt, null, maxScopedRtIds: 0,
            out var off, out _));
        Assert.Null(off.RtIds);

        Assert.True(RetroactiveWriteDetector.TryBuildDirtyWindow(
            Watermark, new[] { Late(8) }, RecomputeChangeSource.Pipeline, DetectedAt, null, out var legacy, out _));
        Assert.Null(legacy.RtIds);
    }

    // ---- MongoDB records ------------------------------------------------------------------------

    [Fact]
    public void DirtyWindow_RoundTripsItsRtIds_AndAPre126RecordReadsUnscoped()
    {
        var window = new ArchiveDirtyWindow(Late(8), Late(9), RecomputeChangeKind.RetroactiveModify,
            RecomputeChangeSource.Pipeline, DetectedAt) { RtIds = RtIdSet.Of(new[] { M2, M1 }) };

        Assert.Equal(window, MongoArchiveRecomputeStateStore.FromRecord(MongoArchiveRecomputeStateStore.ToRecord(window)));

        var legacy = new RtCkArchiveDirtyWindowRecord
        {
            WindowStart = Late(8),
            WindowEnd = Late(9),
            ChangeKind = RtCkRecomputeChangeKindEnum.RetroactiveModify,
            Source = RtCkRecomputeChangeSourceEnum.Pipeline,
            DetectedAt = DetectedAt,
        };
        Assert.Null(MongoArchiveRecomputeStateStore.FromRecord(legacy).RtIds);
    }

    [Fact]
    public void RecomputeRange_RoundTripsItsSetAndDebounce_SoAReadRangeRemovesItselfByValue()
    {
        var rollup = OctoObjectId.GenerateNewId();
        var range = new ArchiveRecomputeRange(rollup, Late(8), Late(9), null, DetectedAt, 0, DetectedAt.AddMinutes(5))
        {
            RtIdSet = RtIdSet.Of(new[] { M1, M3 }),
        };

        var back = MongoArchiveRecomputeStateStore.FromRecord(MongoArchiveRecomputeStateStore.ToRecord(range));

        Assert.Equal(range, back);
        Assert.True(back.IsDebounced);
    }

    // ---- SQL of the in-place refresh --------------------------------------------------------------

    [Fact]
    public void ScopeCondition_RendersASortedInList_ForASet_AndTheSingleScopeAsBefore()
    {
        Assert.Equal("\"rtid\" IN ('a', 'b')", RollupAggregationSqlBuilder.ScopeCondition(null, new[] { "b", "a", "b" }));
        Assert.Equal("\"rtid\" = 'x'", RollupAggregationSqlBuilder.ScopeCondition("x", null));
        Assert.Null(RollupAggregationSqlBuilder.ScopeCondition(null, null));
        Assert.Null(RollupAggregationSqlBuilder.ScopeCondition("", Array.Empty<string>()));
        Assert.Equal("\"rtid\" IN ('o''brien')", RollupAggregationSqlBuilder.ScopeCondition(null, new[] { "o'brien" }));
    }

    [Fact]
    public void TheAggregation_IsRestrictedToTheSet()
    {
        var specs = new[] { new CkRollupAggregationSpec("Value", CkRollupFunction.Sum, "value_sum") };
        var resolved = specs.Select(s => RollupAggregationColumns.BindDeclared(s)).ToList();

        var sql = RollupAggregationSqlBuilder.Build("\"t\".\"src\"", "\"t\".\"stg\"", "Test/Meter-1", resolved,
            Late(8), Late(9), sourceUsesWindowedStorage: false, rtIdSet: new[] { "m2", "m1" });
        var count = RollupAggregationSqlBuilder.BuildExpectedRowCount("\"t\".\"src\"", resolved, Late(8), Late(9),
            sourceUsesWindowedStorage: false, rtIdSet: new[] { "m2", "m1" });

        Assert.Contains("AND \"rtid\" IN ('m1', 'm2')", sql, StringComparison.Ordinal);
        Assert.Contains("AND \"rtid\" IN ('m1', 'm2')", count, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLocfAggregation_IsRestrictedToTheSet_InTheCarryAndTheBucket()
    {
        var specs = new[] { new CkRollupAggregationSpec("Value", CkRollupFunction.TimeWeightedAvg, "value_twa") };
        var resolved = specs.Select(s => RollupAggregationColumns.BindDeclared(s)).ToList();

        var sql = RollupAggregationSqlBuilder.Build("\"t\".\"src\"", "\"t\".\"stg\"", "Test/Meter-1", resolved,
            Late(8), Late(9), sourceUsesWindowedStorage: false, rtIdSet: new[] { "m1" });

        // The carry-in sub-select and the in-bucket rows both read the late series only.
        Assert.True(sql.Split("\"rtid\" IN ('m1')").Length - 1 >= 2, sql);
    }

    [Fact]
    public void ActiveGeneration_IsTheReadersCase_WithoutThePredicate()
    {
        var ranges = new[]
        {
            new GenerationRange(1000, 2000, "", 1),
            new GenerationRange(1000, 2000, "m3", 2),
        };

        Assert.Equal("0", GenerationFilterSql.RenderActiveGeneration(Array.Empty<GenerationRange>()));
        Assert.Equal("\"generation\" = 0", GenerationFilterSql.Render(Array.Empty<GenerationRange>()));
        Assert.Equal(
            "CASE WHEN (\"window_start\" >= 1000 AND \"window_start\" < 2000) AND \"rtid\" = 'm3' THEN 2 " +
            "WHEN (\"window_start\" >= 1000 AND \"window_start\" < 2000) THEN 1 ELSE 0 END",
            GenerationFilterSql.RenderActiveGeneration(ranges));
        Assert.Equal("\"generation\" = " + GenerationFilterSql.RenderActiveGeneration(ranges), GenerationFilterSql.Render(ranges));
    }

    [Fact]
    public void TheUpsert_WritesAtTheActiveGeneration_AndReplacesOnlyTheValueColumns()
    {
        var sql = RollupRecomputeSqlBuilder.BuildUpsertFromStagingAtActiveGenerationSlice(
            "\"t\".\"live\"", "\"t\".\"stg\"",
            new[] { "window_start", "window_end", "was_updated", "rtid", "cktypeid", "value_sum" },
            "CASE WHEN x THEN 2 ELSE 0 END", Late(8), Late(9));

        Assert.StartsWith(
            "INSERT INTO \"t\".\"live\" (\"window_start\", \"window_end\", \"was_updated\", \"rtid\", \"cktypeid\", \"value_sum\", \"generation\") " +
            "SELECT \"window_start\", \"window_end\", \"was_updated\", \"rtid\", \"cktypeid\", \"value_sum\", CASE WHEN x THEN 2 ELSE 0 END FROM \"t\".\"stg\" WHERE ",
            sql, StringComparison.Ordinal);
        Assert.EndsWith(
            "ON CONFLICT (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"generation\") " +
            "DO UPDATE SET \"was_updated\" = EXCLUDED.\"was_updated\", \"value_sum\" = EXCLUDED.\"value_sum\";",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWriteCheck_CountsOnlyRowsAtLeastAsNewAsTheMarker()
    {
        var sql = RollupRecomputeSqlBuilder.BuildCountRefreshedInScope(
            "\"t\".\"live\"", Late(8), Late(9), "\"rtid\" IN ('m1')", "\"generation\" = 0", 1700000000000);

        Assert.StartsWith("SELECT COUNT(*) FROM \"t\".\"live\" WHERE ", sql, StringComparison.Ordinal);
        Assert.EndsWith("AND \"rtid\" IN ('m1') AND \"generation\" = 0 AND \"rtchangeddatetime\" >= 1700000000000;", sql,
            StringComparison.Ordinal);
        Assert.Equal("SELECT CAST(MIN(\"rtchangeddatetime\") AS BIGINT) AS \"marker\" FROM \"t\".\"stg\";",
            RollupRecomputeSqlBuilder.BuildReadRefreshMarker("\"t\".\"stg\""));
    }

    [Fact]
    public void TheStaleRows_AreTheScopedRowsAtTheActiveGenerationOlderThanTheMarker_OrAllWhenNothingWasStaged()
    {
        var withMarker = RollupRecomputeSqlBuilder.BuildStaleInScope(
            "\"t\".\"live\"", Late(8), Late(9), "\"rtid\" IN ('m1')", "\"generation\" = 0", 1700000000000, delete: true);
        var nothingStaged = RollupRecomputeSqlBuilder.BuildStaleInScope(
            "\"t\".\"live\"", Late(8), Late(9), "\"rtid\" IN ('m1')", "\"generation\" = 0", null, delete: false);

        Assert.StartsWith("DELETE FROM \"t\".\"live\" WHERE ", withMarker, StringComparison.Ordinal);
        Assert.EndsWith("AND \"rtid\" IN ('m1') AND \"generation\" = 0 AND " +
                        "(\"rtchangeddatetime\" < 1700000000000 OR \"rtchangeddatetime\" IS NULL);", withMarker, StringComparison.Ordinal);
        Assert.StartsWith("SELECT COUNT(*) FROM \"t\".\"live\" WHERE ", nothingStaged, StringComparison.Ordinal);
        Assert.EndsWith("AND \"rtid\" IN ('m1') AND \"generation\" = 0;", nothingStaged, StringComparison.Ordinal);
    }
}
