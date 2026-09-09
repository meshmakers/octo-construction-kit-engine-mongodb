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
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#5157 §3 — per-source resolution on the CrateDB write side. A rollup's logical spec
/// (<c>Amount.Value</c>, Sum) binds to the source's own column over a base archive (rule 1,
/// byte-identical SQL to before AB#5157) and to the child's target column(s) over a rollup source
/// that stores the same logical aggregation (rule 2, function-preserving read). Every case the
/// activation validator accepts must produce SQL here; an unresolvable spec throws and never
/// silently emits a NULL column.
/// </summary>
public class RollupAggregationPerSourceResolutionTests
{
    private const string SourceTable = "\"acmecorp\".\"archive_source\"";
    private const string TargetTable = "\"acmecorp\".\"archive_target\"";
    private const string RollupCkTypeId = "System.StreamData/CkRollupArchive-1";

    private static readonly DateTime BucketStart = new(2026, 5, 11, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BucketEnd = new(2026, 5, 11, 15, 0, 0, DateTimeKind.Utc);

    private static readonly RtCkId<CkTypeId> MeterType = new("Test", new CkTypeId("EnergyMeter"));
    private static readonly OctoObjectId BaseId = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId ChildId = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId ParentId = OctoObjectId.GenerateNewId();

    // ---- fixtures ----

    /// <summary>A time-range base archive declaring the given logical paths.</summary>
    private static ArchiveSnapshot Base(params string[] paths) =>
        new(BaseId, MeterType, CkArchiveStatus.Activated, "base",
            paths.Select(p => new CkArchiveColumnSpec(p, Indexed: true, Required: false)).ToList())
        {
            IsTimeRange = true,
            Period = TimeSpan.FromMinutes(15),
        };

    /// <summary>A raw (event-based) base archive declaring the given logical paths.</summary>
    private static ArchiveSnapshot RawBase(params string[] paths) =>
        new(BaseId, MeterType, CkArchiveStatus.Activated, "raw",
            paths.Select(p => new CkArchiveColumnSpec(p, Indexed: true, Required: false)).ToList());

    /// <summary>
    /// A rollup source: its archive snapshot declares the generated physical columns (as the Mongo
    /// store maps them), its rollup snapshot carries the logical specs.
    /// </summary>
    private static (ArchiveSnapshot Archive, RollupArchiveSnapshot Rollup) ChildRollup(params CkRollupAggregationSpec[] specs)
    {
        var archive = new ArchiveSnapshot(ChildId, MeterType, CkArchiveStatus.Activated, "hourly",
            RollupColumnGenerator.Generate(specs))
        {
            RollupAggregations = specs,
            Period = TimeSpan.FromHours(1),
        };
        var rollup = new RollupArchiveSnapshot(
            ChildId, MeterType, CkArchiveStatus.Activated, "hourly",
            new[] { new RollupSourceReference(BaseId) },
            TimeSpan.FromHours(1), TimeSpan.Zero, null, specs, null);
        return (archive, rollup);
    }

    private static string BuildFor(
        IReadOnlyList<CkRollupAggregationSpec> parentSpecs, ArchiveSnapshot source, RollupArchiveSnapshot? sourceRollup)
    {
        var resolved = RollupAggregationColumns.ResolveForSource(parentSpecs, ParentId, source, sourceRollup);
        return RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, resolved, BucketStart, BucketEnd,
            source.UsesWindowedStorage);
    }

    private static string BuildLegacy(IReadOnlyList<CkRollupAggregationSpec> specs, bool windowed) =>
        RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, specs, BucketStart, BucketEnd, windowed);

    private static CkRollupAggregationSpec Spec(string path, CkRollupFunction fn, string? target = null, string? comparison = null) =>
        new(path, fn, target, comparison);

    // ---- rule 1: base source ----

    [Fact]
    public void BaseSource_LogicalSum_ReadsDeclaredColumn_AsBefore()
    {
        var specs = new[] { Spec("Amount.Value", CkRollupFunction.Sum) };
        var resolved = RollupAggregationColumns.ResolveForSource(specs, ParentId, Base("Amount.Value"), null);

        Assert.Equal(RollupSourceColumnResolutionKind.DeclaredColumn, resolved[0].Kind);
        var column = Assert.Single(resolved[0].Columns);
        Assert.Equal(new RollupBoundTargetColumn("amountvalue_sum", "SUM", "amountvalue"), column);

        var sql = BuildFor(specs, Base("Amount.Value"), null);
        Assert.Contains("SUM(\"amountvalue\") AS \"amountvalue_sum\"", sql);
    }

    public static IEnumerable<object[]> LegacyParitySpecSets()
    {
        yield return new object[] { new[] { Spec("Amount.Value", CkRollupFunction.Sum) } };
        yield return new object[] { new[] { Spec("Amount.Value", CkRollupFunction.Avg), Spec("Amount.Value", CkRollupFunction.Count) } };
        yield return new object[] { new[] { Spec("Amount.Value", CkRollupFunction.Min), Spec("Amount.Value", CkRollupFunction.Max, "peak") } };
        yield return new object[] { new[] { Spec("Amount.Value", CkRollupFunction.First), Spec("Amount.Value", CkRollupFunction.Last) } };
        yield return new object[] { new[] { Spec("Amount.Value", CkRollupFunction.TimeWeightedAvg), Spec("Amount.Value", CkRollupFunction.Sum) } };
        yield return new object[] { new[] { Spec("Amount.Value", CkRollupFunction.StateDuration, null, "2"), Spec("Amount.Value", CkRollupFunction.Last) } };
    }

    [Theory]
    [MemberData(nameof(LegacyParitySpecSets))]
    public void BaseSource_PerSourceBinding_IsByteIdenticalToTheDeclaredBindingOverload(CkRollupAggregationSpec[] specs)
    {
        // Pin: a rule-1 source produces byte-identical SQL whether the caller goes through the
        // per-source binding or the spec overload (which binds declared) — over a windowed
        // (time-range) base and over a raw base (which for TWA / StateDuration takes the LOCF path
        // and for First / Last the rank sub-select). The pre-AB#5157 emission itself stays pinned by
        // the untouched RollupAggregationSqlBuilderTests, including its exact-equality facts.
        Assert.Equal(BuildLegacy(specs, windowed: true), BuildFor(specs, Base("Amount.Value"), null));
        Assert.Equal(BuildLegacy(specs, windowed: false), BuildFor(specs, RawBase("Amount.Value"), null));
    }

    [Fact]
    public void RollupSource_PhysicalNameChainedSpec_UsesRule1_Unchanged()
    {
        // The pre-AB#5157 chained style: the parent names the child's physical column and applies
        // Sum to it. Rule 1 wins — SQL identical to the legacy overload over a windowed source.
        var (childArchive, childRollup) = ChildRollup(Spec("Amount.Value", CkRollupFunction.Sum));
        var specs = new[] { Spec("amountvalue_sum", CkRollupFunction.Sum) };

        var resolved = RollupAggregationColumns.ResolveForSource(specs, ParentId, childArchive, childRollup);
        Assert.Equal(RollupSourceColumnResolutionKind.DeclaredColumn, resolved[0].Kind);

        var sql = BuildFor(specs, childArchive, childRollup);
        Assert.Contains("SUM(\"amountvalue_sum\") AS \"amountvalue_sum_sum\"", sql);
        Assert.Equal(BuildLegacy(specs, windowed: true), sql);
    }

    // ---- rule 2: rollup source, function-preserving ----

    [Fact]
    public void RollupSource_LogicalSum_ReadsChildSumColumn()
    {
        var (childArchive, childRollup) = ChildRollup(Spec("Amount.Value", CkRollupFunction.Sum));
        var specs = new[] { Spec("Amount.Value", CkRollupFunction.Sum) };

        var resolved = RollupAggregationColumns.ResolveForSource(specs, ParentId, childArchive, childRollup);
        Assert.Equal(RollupSourceColumnResolutionKind.ChildAggregation, resolved[0].Kind);
        Assert.Equal(new RollupBoundTargetColumn("amountvalue_sum", "SUM", "amountvalue_sum"), Assert.Single(resolved[0].Columns));

        var sql = BuildFor(specs, childArchive, childRollup);
        Assert.Contains("SUM(\"amountvalue_sum\") AS \"amountvalue_sum\"", sql);
        Assert.DoesNotContain("SUM(\"amountvalue\")", sql);
        // Windowed source: fully-contained predicate + was_updated propagation, as for any rollup source.
        Assert.Contains("WHERE \"window_start\" >= '2026-05-11T14:00:00.0000000Z'::timestamp AND \"window_end\" <= '2026-05-11T15:00:00.0000000Z'::timestamp", sql);
        Assert.Contains("MAX(\"was_updated\") AS \"was_updated\"", sql);
    }

    [Fact]
    public void RollupSource_LogicalCount_SumsChildCountColumn()
    {
        var (childArchive, childRollup) = ChildRollup(Spec("Amount.Value", CkRollupFunction.Count));
        var specs = new[] { Spec("Amount.Value", CkRollupFunction.Count) };

        var sql = BuildFor(specs, childArchive, childRollup);

        // Count over counts is a SUM, never a COUNT of child rows.
        Assert.Contains("SUM(\"amountvalue_count\") AS \"amountvalue_count\"", sql);
        Assert.DoesNotContain("COUNT(", sql);
    }

    [Fact]
    public void RollupSource_LogicalAvgOverAvg_SumsChildSumAndCountPair()
    {
        var (childArchive, childRollup) = ChildRollup(Spec("Amount.Value", CkRollupFunction.Avg));
        var specs = new[] { Spec("Amount.Value", CkRollupFunction.Avg) };

        var resolved = RollupAggregationColumns.ResolveForSource(specs, ParentId, childArchive, childRollup);
        Assert.Equal(
            new[]
            {
                new RollupBoundTargetColumn("amountvalue_avg_sum", "SUM", "amountvalue_avg_sum"),
                new RollupBoundTargetColumn("amountvalue_avg_count", "SUM", "amountvalue_avg_count"),
            },
            resolved[0].Columns);

        var sql = BuildFor(specs, childArchive, childRollup);
        Assert.Contains("SUM(\"amountvalue_avg_sum\") AS \"amountvalue_avg_sum\"", sql);
        Assert.Contains("SUM(\"amountvalue_avg_count\") AS \"amountvalue_avg_count\"", sql);
        Assert.DoesNotContain("COUNT(", sql);
        Assert.DoesNotContain(" AVG(", sql);
    }

    [Fact]
    public void RollupSource_LogicalMinMax_NestMinMaxOverChildColumns()
    {
        var (childArchive, childRollup) = ChildRollup(
            Spec("Amount.Value", CkRollupFunction.Min), Spec("Amount.Value", CkRollupFunction.Max));
        var specs = new[] { Spec("Amount.Value", CkRollupFunction.Min), Spec("Amount.Value", CkRollupFunction.Max) };

        var sql = BuildFor(specs, childArchive, childRollup);

        Assert.Contains("MIN(\"amountvalue_min\") AS \"amountvalue_min\"", sql);
        Assert.Contains("MAX(\"amountvalue_max\") AS \"amountvalue_max\"", sql);
    }

    [Fact]
    public void RollupSource_LogicalFirstLast_PickChildValueAtEarliestAndLatestChildWindow()
    {
        var (childArchive, childRollup) = ChildRollup(
            Spec("Amount.Value", CkRollupFunction.First), Spec("Amount.Value", CkRollupFunction.Last));
        var specs = new[] { Spec("Amount.Value", CkRollupFunction.First), Spec("Amount.Value", CkRollupFunction.Last) };

        var sql = BuildFor(specs, childArchive, childRollup);

        // arg_min / arg_max over the child window order: rank sub-select ordered by the child
        // window boundary (window_end — identical order to window_start for non-overlapping child
        // windows), value of the rank-1 row.
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY \"rtid\" ORDER BY \"window_end\" ASC) AS \"_rn_first\"", sql);
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY \"rtid\" ORDER BY \"window_end\" DESC) AS \"_rn_last\"", sql);
        Assert.Contains("MAX(CASE WHEN \"_rn_first\" = 1 THEN \"amountvalue_first\" END) AS \"amountvalue_first\"", sql);
        Assert.Contains("MAX(CASE WHEN \"_rn_last\" = 1 THEN \"amountvalue_last\" END) AS \"amountvalue_last\"", sql);
        // Both child columns are projected by the rank sub-select.
        Assert.Contains("\"amountvalue_first\", \"amountvalue_last\",", sql);
        Assert.DoesNotContain("is_carry", sql);
    }

    [Fact]
    public void RollupSource_LogicalTimeWeightedAvg_SumsChildIntegralAndDurationPair()
    {
        var (childArchive, childRollup) = ChildRollup(Spec("Dimming.Level", CkRollupFunction.TimeWeightedAvg));
        var specs = new[] { Spec("Dimming.Level", CkRollupFunction.TimeWeightedAvg) };

        var resolved = RollupAggregationColumns.ResolveForSource(specs, ParentId, childArchive, childRollup);
        Assert.Equal(
            new[]
            {
                new RollupBoundTargetColumn("dimminglevel_twavg_integral", "SUM", "dimminglevel_twavg_integral"),
                new RollupBoundTargetColumn("dimminglevel_twavg_duration", "SUM", "dimminglevel_twavg_duration"),
            },
            resolved[0].Columns);

        var sql = BuildFor(specs, childArchive, childRollup);
        Assert.Contains("SUM(\"dimminglevel_twavg_integral\") AS \"dimminglevel_twavg_integral\"", sql);
        Assert.Contains("SUM(\"dimminglevel_twavg_duration\") AS \"dimminglevel_twavg_duration\"", sql);
        // No re-weighting: the child already integrated over its windows.
        Assert.DoesNotContain("::bigint - \"window_start\"::bigint", sql);
        Assert.DoesNotContain("dt_ms", sql);
        Assert.DoesNotContain("is_carry", sql);
    }

    [Fact]
    public void RollupSource_LogicalStateDuration_SumsChildDurationColumn_NoComparison()
    {
        var (childArchive, childRollup) = ChildRollup(Spec("Lamp.On", CkRollupFunction.StateDuration, null, "true"));
        var specs = new[] { Spec("Lamp.On", CkRollupFunction.StateDuration, null, "true") };

        var sql = BuildFor(specs, childArchive, childRollup);

        // The child already measured the state; the parent adds the child durations up.
        Assert.Contains("SUM(\"lampon_stateduration\") AS \"lampon_stateduration\"", sql);
        Assert.DoesNotContain("CASE WHEN \"lampon_stateduration\" =", sql);
        Assert.DoesNotContain("is_carry", sql);
    }

    [Fact]
    public void RollupSource_StateDurationOverAnotherComparedState_Throws()
    {
        // The child measured how long the lamp was OFF; summing that into the parent's ON column
        // would be silently wrong numbers, so the spec must not resolve against this source.
        var (childArchive, childRollup) = ChildRollup(Spec("Lamp.On", CkRollupFunction.StateDuration, null, "false"));
        var specs = new[] { Spec("Lamp.On", CkRollupFunction.StateDuration, null, "true") };

        var ex = Assert.Throws<InvalidOperationException>(
            () => RollupAggregationColumns.ResolveForSource(specs, ParentId, childArchive, childRollup));

        Assert.Contains("Lamp.On", ex.Message);
        Assert.Contains(ChildId.ToString(), ex.Message);
    }

    [Fact]
    public void RollupSource_StateDurationOverTheSameComparedState_Resolves()
    {
        var (childArchive, childRollup) = ChildRollup(
            Spec("Lamp.On", CkRollupFunction.StateDuration, "lamp_off", "false"),
            Spec("Lamp.On", CkRollupFunction.StateDuration, "lamp_on", "true"));
        var specs = new[] { Spec("Lamp.On", CkRollupFunction.StateDuration, null, "true") };

        var sql = BuildFor(specs, childArchive, childRollup);

        // The child spec picked is the one comparing the SAME state, not the first path match.
        Assert.Contains("SUM(\"lamp_on\") AS \"lampon_stateduration\"", sql);
        Assert.DoesNotContain("\"lamp_off\"", sql);
    }

    [Fact]
    public void RollupSource_ChildExplicitTargetColumnName_IsHonouredAsParentSourceColumn()
    {
        var (childArchive, childRollup) = ChildRollup(Spec("Amount.Value", CkRollupFunction.Sum, "energy_total"));
        var specs = new[] { Spec("Amount.Value", CkRollupFunction.Sum) };

        var sql = BuildFor(specs, childArchive, childRollup);

        Assert.Contains("SUM(\"energy_total\") AS \"amountvalue_sum\"", sql);
        Assert.DoesNotContain("\"amountvalue_sum\") AS", sql);
    }

    [Fact]
    public void RollupSource_ParentExplicitTargetColumnName_StillNamesTheParentColumn()
    {
        var (childArchive, childRollup) = ChildRollup(Spec("Amount.Value", CkRollupFunction.Avg, "energy"));
        var specs = new[] { Spec("Amount.Value", CkRollupFunction.Avg, "daily_energy") };

        var sql = BuildFor(specs, childArchive, childRollup);

        Assert.Contains("SUM(\"energy_sum\") AS \"daily_energy_sum\"", sql);
        Assert.Contains("SUM(\"energy_count\") AS \"daily_energy_count\"", sql);
    }

    [Fact]
    public void SameParentSpecs_OverBaseAndOverRollup_WriteTheSameTargetColumns()
    {
        // The AC1 cutover shape: one parent, legacy time-range history on one validity span and the
        // hourly rollup on the other. Both sources must fill the same INSERT column list.
        var (childArchive, childRollup) = ChildRollup(
            Spec("Amount.Value", CkRollupFunction.Sum), Spec("Amount.Value", CkRollupFunction.Avg));
        var specs = new[] { Spec("Amount.Value", CkRollupFunction.Sum), Spec("Amount.Value", CkRollupFunction.Avg) };

        var overBase = BuildFor(specs, Base("Amount.Value"), null);
        var overRollup = BuildFor(specs, childArchive, childRollup);

        static string InsertLine(string sql) => sql[..sql.IndexOf('\n')];
        Assert.Equal(InsertLine(overBase), InsertLine(overRollup));
        Assert.Contains("SUM(\"amountvalue\") AS \"amountvalue_sum\", SUM(\"amountvalue\") AS \"amountvalue_avg_sum\", COUNT(\"amountvalue\") AS \"amountvalue_avg_count\"", overBase);
        Assert.Contains("SUM(\"amountvalue_sum\") AS \"amountvalue_sum\", SUM(\"amountvalue_avg_sum\") AS \"amountvalue_avg_sum\", SUM(\"amountvalue_avg_count\") AS \"amountvalue_avg_count\"", overRollup);
    }

    // ---- unresolvable ----

    [Fact]
    public void RollupSource_ParentAvgOverChildSumOnly_Throws_NamingSpecSourceAndRollup()
    {
        var (childArchive, childRollup) = ChildRollup(Spec("Amount.Value", CkRollupFunction.Sum));
        var specs = new[] { Spec("Amount.Value", CkRollupFunction.Avg) };

        var ex = Assert.Throws<InvalidOperationException>(
            () => RollupAggregationColumns.ResolveForSource(specs, ParentId, childArchive, childRollup));

        Assert.Contains("Amount.Value", ex.Message);
        Assert.Contains("Avg", ex.Message);
        Assert.Contains(ChildId.ToString(), ex.Message);
        Assert.Contains(ParentId.ToString(), ex.Message);
        Assert.Contains("rollup", ex.Message);
    }

    [Fact]
    public void BaseSource_LackingThePath_Throws()
    {
        var specs = new[] { Spec("Amount.Value", CkRollupFunction.Sum) };

        var ex = Assert.Throws<InvalidOperationException>(
            () => RollupAggregationColumns.ResolveForSource(specs, ParentId, Base("Voltage"), null));

        Assert.Contains("Amount.Value", ex.Message);
        Assert.Contains(BaseId.ToString(), ex.Message);
        Assert.Contains("time-range archive", ex.Message);
    }

    [Fact]
    public void RollupSource_WithoutRollupSnapshot_LogicalSpec_Throws_ExplainingRule1Only()
    {
        var (childArchive, _) = ChildRollup(Spec("Amount.Value", CkRollupFunction.Sum));
        var specs = new[] { Spec("Amount.Value", CkRollupFunction.Sum) };

        var ex = Assert.Throws<InvalidOperationException>(
            () => RollupAggregationColumns.ResolveForSource(specs, ParentId, childArchive, null));

        Assert.Contains("no rollup snapshot", ex.Message);
    }

    [Fact]
    public void RollupSnapshotOfAnotherArchive_IsRejected()
    {
        var (_, childRollup) = ChildRollup(Spec("Amount.Value", CkRollupFunction.Sum));
        var specs = new[] { Spec("Amount.Value", CkRollupFunction.Sum) };

        Assert.Throws<ArgumentException>(
            () => RollupAggregationColumns.ResolveForSource(specs, ParentId, Base("Amount.Value"), childRollup));
    }

    [Fact]
    public void Build_ChildAggregationBindingOverRawStorage_IsRejected()
    {
        var (childArchive, childRollup) = ChildRollup(Spec("Amount.Value", CkRollupFunction.Sum));
        var resolved = RollupAggregationColumns.ResolveForSource(
            new[] { Spec("Amount.Value", CkRollupFunction.Sum) }, ParentId, childArchive, childRollup);

        Assert.Throws<ArgumentException>(() => RollupAggregationSqlBuilder.Build(
            SourceTable, TargetTable, RollupCkTypeId, resolved, BucketStart, BucketEnd,
            sourceUsesWindowedStorage: false));
    }

    // ---- repository wiring: AggregateBucketAsync resolves per source ----

    private sealed class RepositoryHarness
    {
        public readonly IStreamDataDatabaseClient Db = A.Fake<IStreamDataDatabaseClient>();
        public readonly IArchiveRuntimeStore ArchiveStore = A.Fake<IArchiveRuntimeStore>();
        public readonly IRollupArchiveRuntimeStore RollupStore = A.Fake<IRollupArchiveRuntimeStore>();

        public CrateDbStreamDataRepository NewSut() =>
            new(NullLogger<CrateDbStreamDataRepository>.Instance,
                A.Fake<ICkCacheService>(), Db, A.Fake<IStreamDataDatabaseManagementClient>(),
                Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" }),
                "tenant-x", ArchiveStore, A.Fake<IFormulaEngine>(), RollupStore);
    }

    private static RollupArchiveSnapshot Parent(params CkRollupAggregationSpec[] specs) =>
        new(ParentId, MeterType, CkArchiveStatus.Activated, "daily",
            new[] { new RollupSourceReference(BaseId, ValidTo: BucketStart), new RollupSourceReference(ChildId, ValidFrom: BucketStart) },
            TimeSpan.FromDays(1), TimeSpan.Zero, null, specs, null);

    [Fact]
    public async Task AggregateBucketAsync_RollupSource_LoadsItsRollupSnapshot_AndReadsChildColumns()
    {
        var h = new RepositoryHarness();
        var (childArchive, childRollup) = ChildRollup(Spec("Amount.Value", CkRollupFunction.Sum));
        var parent = Parent(Spec("Amount.Value", CkRollupFunction.Sum));
        A.CallTo(() => h.RollupStore.GetAsync(ChildId)).Returns(childRollup);
        // No computed columns on the parent (the post-aggregation evaluation bails out on null).
        A.CallTo(() => h.ArchiveStore.GetAsync(ParentId)).Returns(Task.FromResult<ArchiveSnapshot?>(null));
        string? sql = null;
        A.CallTo(() => h.Db.ExecuteNonQueryAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .Invokes(call => sql = call.GetArgument<string>(1))
            .Returns(1);

        await h.NewSut().AggregateBucketAsync(childArchive, parent, BucketStart, BucketEnd, CancellationToken.None);

        Assert.NotNull(sql);
        Assert.Contains("SUM(\"amountvalue_sum\") AS \"amountvalue_sum\"", sql);
        Assert.Contains($"archive_{ChildId}", sql);
        A.CallTo(() => h.RollupStore.GetAsync(ChildId)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task AggregateBucketAsync_BaseSource_DoesNotConsultRollupStore_AndReadsDeclaredColumn()
    {
        var h = new RepositoryHarness();
        var parent = Parent(Spec("Amount.Value", CkRollupFunction.Sum));
        A.CallTo(() => h.ArchiveStore.GetAsync(ParentId)).Returns(Task.FromResult<ArchiveSnapshot?>(null));
        string? sql = null;
        A.CallTo(() => h.Db.ExecuteNonQueryAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .Invokes(call => sql = call.GetArgument<string>(1))
            .Returns(1);

        await h.NewSut().AggregateBucketAsync(Base("Amount.Value"), parent, BucketStart, BucketEnd, CancellationToken.None);

        Assert.NotNull(sql);
        Assert.Contains("SUM(\"amountvalue\") AS \"amountvalue_sum\"", sql);
        A.CallTo(() => h.RollupStore.GetAsync(A<OctoObjectId>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AggregateBucketAsync_UnresolvableSpec_Throws_BeforeAnyDbWrite()
    {
        var h = new RepositoryHarness();
        var (childArchive, childRollup) = ChildRollup(Spec("Amount.Value", CkRollupFunction.Sum));
        var parent = Parent(Spec("Amount.Value", CkRollupFunction.Avg));
        A.CallTo(() => h.RollupStore.GetAsync(ChildId)).Returns(childRollup);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.NewSut().AggregateBucketAsync(childArchive, parent, BucketStart, BucketEnd, CancellationToken.None));

        A.CallTo(() => h.Db.ExecuteNonQueryAsync(A<string>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }
}
