using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Validates the cascade chain walk for ad-hoc aggregation queries against rollup archives.
/// Mirrors the data shape we see in production for SimEnergyMeasurementsMonthly:
/// Monthly → Daily → Hourly → raw, where Hourly is the only rollup that names CK paths
/// directly and Daily / Monthly accumulate physical storage column names.
/// </summary>
public class RollupChainAggregationResolverTests
{
    private static readonly OctoObjectId RawRtId = new("aa00000000000000000001a0");
    private static readonly OctoObjectId HourlyRtId = new("aa00000000000000000001b0");
    private static readonly OctoObjectId DailyRtId = new("aa00000000000000000001c0");
    private static readonly OctoObjectId MonthlyRtId = new("aa00000000000000000001d0");
    private static readonly RtCkId<CkTypeId> CkType = new("Demo/EnergyMeasurement");

    [Fact]
    public async Task DirectRollup_ResolvesSumOnLogicalPath()
    {
        // Single-level rollup over raw. SourcePath references the CK attribute directly.
        var hourly = MakeRollup(HourlyRtId, RawRtId,
            new CkRollupAggregationSpec("amount.value", CkRollupFunction.Sum, "amountvalue_sum"));

        var (getArchive, getRollup) = Stores(MakeRawArchive(RawRtId), hourly);

        var result = await RollupChainAggregationResolver.ResolveAsync(
            hourly, "amount.value", AggregationFunctionDto.Sum,
            getArchive, getRollup, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("SUM(\"amountvalue_sum\")", result!.SqlExpression);
    }

    [Fact]
    public async Task TwoLevelCascade_ResolvesSumOnLogicalPathThroughChain()
    {
        // Hourly: (amount.value, SUM) → column "amountvalue_sum"
        var hourly = MakeRollup(HourlyRtId, RawRtId,
            new CkRollupAggregationSpec("amount.value", CkRollupFunction.Sum, "amountvalue_sum"));
        // Daily: (amountvalue_sum, SUM) → column "amountvalue_sum" (re-uses parent's column name)
        var daily = MakeRollup(DailyRtId, HourlyRtId,
            new CkRollupAggregationSpec("amountvalue_sum", CkRollupFunction.Sum, "amountvalue_sum"));

        var (getArchive, getRollup) = Stores(MakeRawArchive(RawRtId), hourly, daily);

        var result = await RollupChainAggregationResolver.ResolveAsync(
            daily, "amount.value", AggregationFunctionDto.Sum,
            getArchive, getRollup, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("SUM(\"amountvalue_sum\")", result!.SqlExpression);
    }

    [Fact]
    public async Task ThreeLevelCascade_PropagatesLogicalPathToTopLevel()
    {
        // Hourly: SUM/COUNT/MIN/MAX on amount.value (direct over raw)
        var hourly = MakeRollup(HourlyRtId, RawRtId,
            new CkRollupAggregationSpec("amount.value", CkRollupFunction.Sum, "amountvalue_sum"),
            new CkRollupAggregationSpec("amount.value", CkRollupFunction.Count, "amountvalue_count"),
            new CkRollupAggregationSpec("amount.value", CkRollupFunction.Min, "amountvalue_min"),
            new CkRollupAggregationSpec("amount.value", CkRollupFunction.Max, "amountvalue_max"));
        // Daily reads Hourly's physical columns (sum-of-sums, sum-of-counts, min-of-mins, max-of-maxes)
        var daily = MakeRollup(DailyRtId, HourlyRtId,
            new CkRollupAggregationSpec("amountvalue_sum", CkRollupFunction.Sum, "amountvalue_sum"),
            new CkRollupAggregationSpec("amountvalue_count", CkRollupFunction.Sum, "amountvalue_count"),
            new CkRollupAggregationSpec("amountvalue_min", CkRollupFunction.Min, "amountvalue_min"),
            new CkRollupAggregationSpec("amountvalue_max", CkRollupFunction.Max, "amountvalue_max"));
        // Monthly reads Daily's physical columns
        var monthly = MakeRollup(MonthlyRtId, DailyRtId,
            new CkRollupAggregationSpec("amountvalue_sum", CkRollupFunction.Sum, "amountvalue_sum"),
            new CkRollupAggregationSpec("amountvalue_count", CkRollupFunction.Sum, "amountvalue_count"),
            new CkRollupAggregationSpec("amountvalue_min", CkRollupFunction.Min, "amountvalue_min"),
            new CkRollupAggregationSpec("amountvalue_max", CkRollupFunction.Max, "amountvalue_max"));

        var (getArchive, getRollup) = Stores(MakeRawArchive(RawRtId), hourly, daily, monthly);

        // Each function should resolve to the matching physical column at Monthly level.
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("SUM(\"amountvalue_sum\")",
            (await RollupChainAggregationResolver.ResolveAsync(monthly, "amount.value", AggregationFunctionDto.Sum, getArchive, getRollup, ct))!.SqlExpression);
        Assert.Equal("SUM(\"amountvalue_count\")",
            (await RollupChainAggregationResolver.ResolveAsync(monthly, "amount.value", AggregationFunctionDto.Count, getArchive, getRollup, ct))!.SqlExpression);
        Assert.Equal("MIN(\"amountvalue_min\")",
            (await RollupChainAggregationResolver.ResolveAsync(monthly, "amount.value", AggregationFunctionDto.Min, getArchive, getRollup, ct))!.SqlExpression);
        Assert.Equal("MAX(\"amountvalue_max\")",
            (await RollupChainAggregationResolver.ResolveAsync(monthly, "amount.value", AggregationFunctionDto.Max, getArchive, getRollup, ct))!.SqlExpression);
    }

    [Fact]
    public async Task ThreeLevelCascade_AvgComposesFromSumAndCount()
    {
        // Same setup as above; the resolver should reconstruct AVG from the materialised SUM+COUNT pair.
        var hourly = MakeRollup(HourlyRtId, RawRtId,
            new CkRollupAggregationSpec("amount.value", CkRollupFunction.Sum, "amountvalue_sum"),
            new CkRollupAggregationSpec("amount.value", CkRollupFunction.Count, "amountvalue_count"));
        var daily = MakeRollup(DailyRtId, HourlyRtId,
            new CkRollupAggregationSpec("amountvalue_sum", CkRollupFunction.Sum, "amountvalue_sum"),
            new CkRollupAggregationSpec("amountvalue_count", CkRollupFunction.Sum, "amountvalue_count"));
        var monthly = MakeRollup(MonthlyRtId, DailyRtId,
            new CkRollupAggregationSpec("amountvalue_sum", CkRollupFunction.Sum, "amountvalue_sum"),
            new CkRollupAggregationSpec("amountvalue_count", CkRollupFunction.Sum, "amountvalue_count"));

        var (getArchive, getRollup) = Stores(MakeRawArchive(RawRtId), hourly, daily, monthly);

        var result = await RollupChainAggregationResolver.ResolveAsync(
            monthly, "amount.value", AggregationFunctionDto.Avg,
            getArchive, getRollup, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(
            "SUM(\"amountvalue_sum\") / NULLIF(SUM(\"amountvalue_count\"), 0)",
            result!.SqlExpression);
    }

    [Fact]
    public async Task DirectAvgRollup_ResolvesAvgFromMaterialisedPair()
    {
        // Single-level AVG materialises as (_sum, _count) pair.
        var hourly = MakeRollup(HourlyRtId, RawRtId,
            new CkRollupAggregationSpec("amount.value", CkRollupFunction.Avg, null));

        var (getArchive, getRollup) = Stores(MakeRawArchive(RawRtId), hourly);

        var result = await RollupChainAggregationResolver.ResolveAsync(
            hourly, "amount.value", AggregationFunctionDto.Avg,
            getArchive, getRollup, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(
            "SUM(\"amountvalue_avg_sum\") / NULLIF(SUM(\"amountvalue_avg_count\"), 0)",
            result!.SqlExpression);
    }

    [Fact]
    public async Task UnchainableFunction_ReturnsNull()
    {
        // Daily materialised only SUM; asking for MIN is unresolvable (the bucket-min info isn't there).
        var hourly = MakeRollup(HourlyRtId, RawRtId,
            new CkRollupAggregationSpec("amount.value", CkRollupFunction.Sum, "amountvalue_sum"));
        var daily = MakeRollup(DailyRtId, HourlyRtId,
            new CkRollupAggregationSpec("amountvalue_sum", CkRollupFunction.Sum, "amountvalue_sum"));

        var (getArchive, getRollup) = Stores(MakeRawArchive(RawRtId), hourly, daily);

        var result = await RollupChainAggregationResolver.ResolveAsync(
            daily, "amount.value", AggregationFunctionDto.Min,
            getArchive, getRollup, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task BrokenChain_MissingParent_ReturnsNull()
    {
        var monthly = MakeRollup(MonthlyRtId, DailyRtId,
            new CkRollupAggregationSpec("amountvalue_sum", CkRollupFunction.Sum, "amountvalue_sum"));

        // Daily not provided — chain is broken.
        var (getArchive, getRollup) = Stores(MakeRawArchive(RawRtId), monthly);

        var result = await RollupChainAggregationResolver.ResolveAsync(
            monthly, "amount.value", AggregationFunctionDto.Sum,
            getArchive, getRollup, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static RollupArchiveSnapshot MakeRollup(
        OctoObjectId rtId,
        OctoObjectId sourceRtId,
        params CkRollupAggregationSpec[] aggregations)
        => new(
            RtId: rtId,
            TargetCkTypeId: CkType,
            Status: CkArchiveStatus.Activated,
            RtWellKnownName: null,
            SourceArchiveRtId: sourceRtId,
            BucketSize: TimeSpan.FromDays(1),
            WatermarkLag: TimeSpan.FromMinutes(5),
            LastAggregatedBucketEnd: null,
            Aggregations: aggregations,
            FrozenUntil: null);

    private static ArchiveSnapshot MakeRawArchive(OctoObjectId rtId)
        => new(rtId, CkType, CkArchiveStatus.Activated, null, Array.Empty<CkArchiveColumnSpec>());

    private static (Func<OctoObjectId, Task<ArchiveSnapshot?>>, Func<OctoObjectId, Task<RollupArchiveSnapshot?>>) Stores(
        ArchiveSnapshot raw,
        params RollupArchiveSnapshot[] rollups)
    {
        var rollupById = rollups.ToDictionary(r => r.RtId);
        var rollupSnapshotsById = rollups.ToDictionary(
            r => r.RtId,
            r => new ArchiveSnapshot(r.RtId, r.TargetCkTypeId, r.Status, r.RtWellKnownName, Array.Empty<CkArchiveColumnSpec>())
            {
                RollupAggregations = r.Aggregations
            });

        Task<ArchiveSnapshot?> GetArchive(OctoObjectId id)
        {
            if (id == raw.RtId) return Task.FromResult<ArchiveSnapshot?>(raw);
            return Task.FromResult<ArchiveSnapshot?>(rollupSnapshotsById.GetValueOrDefault(id));
        }
        Task<RollupArchiveSnapshot?> GetRollup(OctoObjectId id)
            => Task.FromResult<RollupArchiveSnapshot?>(rollupById.GetValueOrDefault(id));

        return (GetArchive, GetRollup);
    }

    // ---- TimeWeightedAvg (AB#4336) ----

    [Fact]
    public async Task DirectRollup_TimeWeightedAvg_ResolvesIntegralOverDuration()
    {
        var hourly = MakeRollup(HourlyRtId, RawRtId,
            new CkRollupAggregationSpec("dimming.level", CkRollupFunction.TimeWeightedAvg, null));

        var (getArchive, getRollup) = Stores(MakeRawArchive(RawRtId), hourly);

        var result = await RollupChainAggregationResolver.ResolveAsync(
            hourly, "dimming.level", AggregationFunctionDto.TimeWeightedAvg,
            getArchive, getRollup, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(
            "SUM(\"dimminglevel_twavg_integral\") / NULLIF(SUM(\"dimminglevel_twavg_duration\"), 0)",
            result!.SqlExpression);
        Assert.Equal("dimming.level_twavg", result.SqlAlias);
    }

    [Fact]
    public async Task Cascade_TimeWeightedAvgPair_ChainsViaSumSpecs()
    {
        // Hourly materialises TWA over the raw event archive; Daily accumulates the pair via
        // SUM specs on the physical columns — the ratio recombines exactly (AB#4336).
        var hourly = MakeRollup(HourlyRtId, RawRtId,
            new CkRollupAggregationSpec("dimming.level", CkRollupFunction.TimeWeightedAvg, null));
        var daily = MakeRollup(DailyRtId, HourlyRtId,
            new CkRollupAggregationSpec("dimminglevel_twavg_integral", CkRollupFunction.Sum, "dimminglevel_twavg_integral"),
            new CkRollupAggregationSpec("dimminglevel_twavg_duration", CkRollupFunction.Sum, "dimminglevel_twavg_duration"));

        var (getArchive, getRollup) = Stores(MakeRawArchive(RawRtId), hourly, daily);

        var result = await RollupChainAggregationResolver.ResolveAsync(
            daily, "dimming.level", AggregationFunctionDto.TimeWeightedAvg,
            getArchive, getRollup, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(
            "SUM(\"dimminglevel_twavg_integral\") / NULLIF(SUM(\"dimminglevel_twavg_duration\"), 0)",
            result!.SqlExpression);
    }

    [Fact]
    public async Task DirectRollup_AvgPair_DoesNotSatisfyTimeWeightedAvgTarget()
    {
        // Sample-weighted AVG must never masquerade as time-weighted.
        var hourly = MakeRollup(HourlyRtId, RawRtId,
            new CkRollupAggregationSpec("dimming.level", CkRollupFunction.Avg, null));

        var (getArchive, getRollup) = Stores(MakeRawArchive(RawRtId), hourly);

        var result = await RollupChainAggregationResolver.ResolveAsync(
            hourly, "dimming.level", AggregationFunctionDto.TimeWeightedAvg,
            getArchive, getRollup, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }
}
