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
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// A rollup that is the source of the next level is read in its active generation only, and the
/// read is repeated when the source commits a recompute of the bucket meanwhile. Pins the predicate
/// the aggregation statement gets, the pointer check around it, and the rewind that keeps the
/// pointer of the rows it does not rewind. See <c>docs/streamdata-rollup-source-generation-read.md</c>.
/// </summary>
public class RollupSourceGenerationReadTests
{
    private const string Tenant = "acmecorp";
    private const string SourceTable = "\"acmecorp\".\"archive_source\"";
    private const string TargetTable = "\"acmecorp\".\"archive_target\"";
    private const string RollupCkTypeId = "System.StreamData/CkRollupArchive-1";

    private static readonly DateTime BucketStart = new(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BucketEnd = new(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc);
    private static readonly long StartMs = new DateTimeOffset(BucketStart).ToUnixTimeMilliseconds();
    private static readonly long EndMs = new DateTimeOffset(BucketEnd).ToUnixTimeMilliseconds();
    private const long DayMs = 86_400_000;

    private static readonly CkRollupAggregationSpec[] SumVoltage =
        { new("voltage_sum", CkRollupFunction.Sum, null) };

    private static readonly RtCkId<CkTypeId> MeterType = new("Test", new CkTypeId("EnergyMeter"));

    // ---- the predicate on the source scan ----

    [Fact]
    public void Build_NoGenerationRanges_LeavesTheSourceScanAsItWas()
    {
        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, SumVoltage, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: true);

        // A time-range source is windowed too, but its table has no generation column.
        Assert.DoesNotContain("AND \"generation\"", sql);
    }

    [Fact]
    public void Build_RollupSourceWithoutPointer_ReadsGenerationZero()
    {
        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, SumVoltage, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: true, sourceGenerationRanges: Array.Empty<GenerationRange>());

        // Never "no predicate": rows a recompute has copied in but not yet committed must stay out.
        Assert.Contains("AND \"generation\" = 0", sql);
        Assert.True(
            sql.IndexOf("AND \"generation\" = 0", StringComparison.Ordinal)
            < sql.IndexOf("GROUP BY", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RollupSource_SelectsTheGenerationThePointerNames()
    {
        var ranges = new[] { new GenerationRange(StartMs - DayMs, EndMs + DayMs, string.Empty, 7) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, SumVoltage, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: true, sourceGenerationRanges: ranges);

        Assert.Contains(
            $"AND \"generation\" = CASE WHEN (\"window_start\" >= {StartMs - DayMs} AND \"window_start\" < {EndMs + DayMs}) THEN 7 ELSE 0 END",
            sql);
    }

    [Fact]
    public void Build_RollupSource_EmitsOnlyPointersOverlappingTheBucket()
    {
        var ranges = new[]
        {
            new GenerationRange(StartMs - 2 * DayMs, StartMs, string.Empty, 3), // ends where the bucket starts
            new GenerationRange(StartMs, EndMs, string.Empty, 5),               // the bucket
            new GenerationRange(EndMs, EndMs + DayMs, string.Empty, 6),         // starts where the bucket ends
        };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, SumVoltage, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: true, sourceGenerationRanges: ranges);

        Assert.Contains("THEN 5 ELSE 0 END", sql);
        Assert.DoesNotContain("THEN 3", sql);
        Assert.DoesNotContain("THEN 6", sql);
    }

    [Fact]
    public void Build_RollupSource_NoOverlappingPointer_ReadsGenerationZero()
    {
        var ranges = new[] { new GenerationRange(EndMs, EndMs + DayMs, string.Empty, 6) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, SumVoltage, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: true, sourceGenerationRanges: ranges);

        Assert.Contains("AND \"generation\" = 0", sql);
        Assert.DoesNotContain("CASE WHEN (\"window_start\"", sql);
    }

    [Fact]
    public void Build_RollupSource_FirstLast_FiltersInsideTheRankingSubSelect()
    {
        var aggregations = new[] { new CkRollupAggregationSpec("voltage_first", CkRollupFunction.First, null) };
        var ranges = new[] { new GenerationRange(StartMs, EndMs, string.Empty, 4) };

        var sql = RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, aggregations, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: true, sourceGenerationRanges: ranges);

        // The rank must be computed over the active generation only, or the earliest / latest row
        // could be one of a superseded generation.
        var predicate = sql.IndexOf("AND \"generation\" = CASE", StringComparison.Ordinal);
        Assert.True(predicate > sql.IndexOf("ROW_NUMBER()", StringComparison.Ordinal));
        Assert.True(predicate < sql.IndexOf(") \"src\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_GenerationRangesForARawSource_Throws()
    {
        Assert.Throws<ArgumentException>(() => RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, SumVoltage, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false, sourceGenerationRanges: Array.Empty<GenerationRange>()));
    }

    // ---- the pointer check around the statement ----

    [Fact]
    public async Task Aggregate_BaseSource_RunsTheStatementOnce_WithoutReadingAPointer()
    {
        var harness = new Harness();
        var baseSource = new ArchiveSnapshot(OctoObjectId.GenerateNewId(), MeterType, CkArchiveStatus.Activated,
            "base", new[] { new CkArchiveColumnSpec("voltage", Indexed: true, Required: false) })
        {
            IsTimeRange = true,
            Period = TimeSpan.FromMinutes(15),
        };

        var rows = await harness.Aggregator.AggregateAsync(
            baseSource, BucketStart, BucketEnd, harness.BuildSql, harness.Discard, CancellationToken.None);

        Assert.Equal(Harness.RowsPerStatement, rows);
        Assert.Equal(new IReadOnlyList<GenerationRange>?[] { null }, harness.BuiltWith);
        Assert.Equal(0, harness.PointerReads);
        Assert.Equal(0, harness.Discards);
    }

    [Fact]
    public async Task Aggregate_RollupSource_PointerUnchanged_KeepsTheFirstAttempt()
    {
        var pointer = new GenerationRange(StartMs, EndMs, string.Empty, 4);
        var harness = new Harness(new[] { pointer }, new[] { pointer });

        var rows = await harness.Aggregator.AggregateAsync(
            RollupSource(), BucketStart, BucketEnd, harness.BuildSql, harness.Discard, CancellationToken.None);

        Assert.Equal(Harness.RowsPerStatement, rows);
        Assert.Single(harness.BuiltWith);
        Assert.Equal(new[] { pointer }, harness.BuiltWith[0]);
        Assert.Equal(0, harness.Discards);
    }

    [Fact]
    public async Task Aggregate_RollupSource_PointerFlippedDuringTheStatement_AggregatesAgainOnTheNewGeneration()
    {
        var old = new GenerationRange(StartMs, EndMs, string.Empty, 4);
        var flipped = new GenerationRange(StartMs, EndMs, string.Empty, 5);
        // attempt 1: before = 4, after = 5 (discard) — attempt 2: before = 5, after = 5 (keep)
        var harness = new Harness(new[] { old }, new[] { flipped }, new[] { flipped }, new[] { flipped });

        await harness.Aggregator.AggregateAsync(
            RollupSource(), BucketStart, BucketEnd, harness.BuildSql, harness.Discard, CancellationToken.None);

        Assert.Equal(2, harness.BuiltWith.Count);
        Assert.Equal(new[] { old }, harness.BuiltWith[0]);
        Assert.Equal(new[] { flipped }, harness.BuiltWith[1]);
        Assert.Equal(1, harness.Discards);
    }

    [Fact]
    public async Task Aggregate_RollupSource_FirstRecomputeOfTheBucketDuringTheStatement_AggregatesAgain()
    {
        // No pointer before (generation 0), a first pointer after: the same race, from the baseline.
        var first = new GenerationRange(StartMs, EndMs, string.Empty, 1);
        var harness = new Harness(Array.Empty<GenerationRange>(), new[] { first }, new[] { first }, new[] { first });

        await harness.Aggregator.AggregateAsync(
            RollupSource(), BucketStart, BucketEnd, harness.BuildSql, harness.Discard, CancellationToken.None);

        Assert.Equal(2, harness.BuiltWith.Count);
        Assert.Empty(harness.BuiltWith[0]!);
        Assert.Equal(new[] { first }, harness.BuiltWith[1]);
    }

    [Fact]
    public async Task Aggregate_RollupSource_PointerOfAnotherRangeChanges_DoesNotRepeat()
    {
        var mine = new GenerationRange(StartMs, EndMs, string.Empty, 4);
        var elsewhereOld = new GenerationRange(EndMs, EndMs + DayMs, string.Empty, 2);
        var elsewhereNew = new GenerationRange(EndMs, EndMs + DayMs, string.Empty, 9);
        var harness = new Harness(new[] { mine, elsewhereOld }, new[] { mine, elsewhereNew });

        await harness.Aggregator.AggregateAsync(
            RollupSource(), BucketStart, BucketEnd, harness.BuildSql, harness.Discard, CancellationToken.None);

        Assert.Single(harness.BuiltWith);
        Assert.Equal(new[] { mine }, harness.BuiltWith[0]);
    }

    [Fact]
    public async Task Aggregate_RollupSource_NeverSettles_ThrowsInsteadOfKeepingAnUnverifiedResult()
    {
        var reads = Enumerable.Range(1, 2 * RollupSourceBucketAggregator.MaxAttempts)
            .Select(g => new[] { new GenerationRange(StartMs, EndMs, string.Empty, g) })
            .ToArray<IReadOnlyList<GenerationRange>>();
        var harness = new Harness(reads);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Aggregator.AggregateAsync(
            RollupSource(), BucketStart, BucketEnd, harness.BuildSql, harness.Discard, CancellationToken.None));

        Assert.Equal(RollupSourceBucketAggregator.MaxAttempts, harness.BuiltWith.Count);
    }

    [Fact]
    public async Task Aggregate_RollupSource_EnsuresTheSourcePointerTableOncePerSource()
    {
        var pointer = new GenerationRange(StartMs, EndMs, string.Empty, 4);
        var harness = new Harness(new[] { pointer }, new[] { pointer }, new[] { pointer }, new[] { pointer });
        var source = RollupSource();

        await harness.Aggregator.AggregateAsync(source, BucketStart, BucketEnd, harness.BuildSql, null, CancellationToken.None);
        await harness.Aggregator.AggregateAsync(source, BucketStart, BucketEnd, harness.BuildSql, null, CancellationToken.None);

        A.CallTo(() => harness.Management.ExecuteDdlAsync(Tenant, A<string>.That.Contains("__genmap")))
            .MustHaveHappenedOnceExactly();
    }

    // ---- rewind keeps the pointer of the rows it does not rewind ----

    [Fact]
    public void BuildTruncateStraddlingPointers_CopiesThePartBeforeTheBoundary_OnTheSameGenerationAndScope()
    {
        var genMap = GenerationMapSqlBuilder.GenMapTable(Tenant, "rollup1");

        var sql = GenerationMapSqlBuilder.BuildTruncateStraddlingPointers(genMap, BucketStart);

        Assert.Equal(
            $"INSERT INTO {genMap} (\"range_start\", \"range_end\", \"rtid_scope\", \"generation\") " +
            $"SELECT \"range_start\", {StartMs}, \"rtid_scope\", \"generation\" FROM {genMap} " +
            $"WHERE \"range_start\" < {StartMs} AND \"range_end\" > {StartMs} " +
            "ON CONFLICT (\"range_start\", \"range_end\", \"rtid_scope\") DO UPDATE SET " +
            "\"generation\" = GREATEST(\"generation\", excluded.\"generation\");",
            sql);
    }

    [Fact]
    public void BuildDeleteGenerationsFrom_KeepsTheTruncatedPointer()
    {
        var genMap = GenerationMapSqlBuilder.GenMapTable(Tenant, "rollup1");

        var sql = GenerationMapSqlBuilder.BuildDeleteGenerationsFrom(genMap, BucketStart);

        // Strictly greater: the truncated copy ends exactly at the boundary and survives.
        Assert.Equal($"DELETE FROM {genMap} WHERE \"range_end\" > {StartMs};", sql);
    }

    [Fact]
    public void BuildDeleteStagedBucket_RemovesExactlyThatBucket()
    {
        var staging = RollupRecomputeSqlBuilder.StagingTable(Tenant, "rollup1");

        var sql = RollupRecomputeSqlBuilder.BuildDeleteStagedBucket(staging, BucketStart);

        Assert.Equal($"DELETE FROM {staging} WHERE \"window_start\" = {StartMs};", sql);
    }

    // ---- fixtures ----

    private static ArchiveSnapshot RollupSource()
    {
        var specs = new[] { new CkRollupAggregationSpec("voltage", CkRollupFunction.Sum, null) };
        return new ArchiveSnapshot(OctoObjectId.GenerateNewId(), MeterType, CkArchiveStatus.Activated, "hourly",
            RollupColumnGenerator.Generate(specs))
        {
            RollupAggregations = specs,
            Period = TimeSpan.FromHours(1),
        };
    }

    /// <summary>
    /// A fake CrateDB: each pointer read returns the next scripted pointer state, every statement
    /// reports the same row count, and the ranges each statement was built with are recorded.
    /// </summary>
    private sealed class Harness
    {
        public const int RowsPerStatement = 3;

        private readonly Queue<IReadOnlyList<GenerationRange>> _pointerStates;

        public Harness(params IReadOnlyList<GenerationRange>[] pointerStates)
        {
            _pointerStates = new Queue<IReadOnlyList<GenerationRange>>(pointerStates);

            A.CallTo(() => Client.StreamRawRowsAsync(Tenant, A<string>._, A<CancellationToken>._))
                .ReturnsLazily(() =>
                {
                    PointerReads++;
                    return ToRows(_pointerStates.Dequeue());
                });
            A.CallTo(() => Client.ExecuteNonQueryAsync(Tenant, A<string>._, A<CancellationToken>._))
                .Returns(RowsPerStatement);

            Aggregator = new RollupSourceBucketAggregator(Tenant, Client, Management, NullLogger.Instance);
        }

        public IStreamDataDatabaseClient Client { get; } = A.Fake<IStreamDataDatabaseClient>();

        public IStreamDataDatabaseManagementClient Management { get; } = A.Fake<IStreamDataDatabaseManagementClient>();

        public RollupSourceBucketAggregator Aggregator { get; }

        public List<IReadOnlyList<GenerationRange>?> BuiltWith { get; } = new();

        public int PointerReads { get; private set; }

        public int Discards { get; private set; }

        public string BuildSql(IReadOnlyList<GenerationRange>? ranges)
        {
            BuiltWith.Add(ranges);
            return "INSERT …";
        }

        public Task Discard(CancellationToken cancellationToken)
        {
            Discards++;
            return Task.CompletedTask;
        }

        private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ToRows(
            IReadOnlyList<GenerationRange> ranges)
        {
            foreach (var range in ranges)
            {
                yield return new Dictionary<string, object?>
                {
                    ["range_start"] = range.StartMs,
                    ["range_end"] = range.EndMs,
                    ["rtid_scope"] = range.Scope,
                    ["generation"] = range.Generation,
                };
            }

            await Task.CompletedTask;
        }
    }
}
