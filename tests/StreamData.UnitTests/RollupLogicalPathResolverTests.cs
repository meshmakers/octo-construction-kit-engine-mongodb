using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Validates the chain walk that reverses physical storage column names back to logical CK
/// attribute paths for cascade rollups. Companion to <see cref="RollupQueryAggregationResolverTests"/>.
/// </summary>
public class RollupLogicalPathResolverTests
{
    private static readonly OctoObjectId RawRtId = new("aa00000000000000000001a0");
    private static readonly OctoObjectId DailyRtId = new("aa00000000000000000001b0");
    private static readonly OctoObjectId MonthlyRtId = new("aa00000000000000000001c0");
    private static readonly RtCkId<CkTypeId> CkType = new("Demo/EnergyMeasurement");

    [Fact]
    public async Task RollupOnRaw_ReturnsSourcePathsAsLogicalPaths()
    {
        // Daily rollup directly over a raw archive. The aggregation specs' SourcePath values are
        // CK attribute paths already — no reverse mapping required.
        var daily = MakeRollup(DailyRtId, RawRtId,
            new CkRollupAggregationSpec("amountValue", CkRollupFunction.Avg, null),
            new CkRollupAggregationSpec("voltage", CkRollupFunction.Sum, null));

        var raw = MakeRawArchive(RawRtId);

        var result = await RollupLogicalPathResolver.ResolveAsync(
            daily,
            getArchive: id => Task.FromResult<ArchiveSnapshot?>(id == RawRtId ? raw : null),
            getRollup: _ => Task.FromResult<RollupArchiveSnapshot?>(null),
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "amountValue", "voltage" }, result);
    }

    [Fact]
    public async Task RollupOnRollup_ReversesPhysicalStorageColumnsToLogicalPaths()
    {
        // Monthly is a cascade rollup over Daily. Its aggregation specs reference Daily's stored
        // columns (e.g. "amountvalue_sum") — the resolver must trace them back to the original
        // CK attribute name via Daily's spec.
        var monthly = MakeRollup(MonthlyRtId, DailyRtId,
            new CkRollupAggregationSpec("amountvalue_sum", CkRollupFunction.Sum, null),
            new CkRollupAggregationSpec("amountvalue_count", CkRollupFunction.Sum, null),
            new CkRollupAggregationSpec("amountvalue_min", CkRollupFunction.Min, null),
            new CkRollupAggregationSpec("amountvalue_max", CkRollupFunction.Max, null));

        var daily = MakeRollup(DailyRtId, RawRtId,
            new CkRollupAggregationSpec("amountValue", CkRollupFunction.Sum, null),
            new CkRollupAggregationSpec("amountValue", CkRollupFunction.Count, null),
            new CkRollupAggregationSpec("amountValue", CkRollupFunction.Min, null),
            new CkRollupAggregationSpec("amountValue", CkRollupFunction.Max, null));

        var dailySnapshot = ToArchiveSnapshot(daily);
        var rawSnapshot = MakeRawArchive(RawRtId);

        var result = await RollupLogicalPathResolver.ResolveAsync(
            monthly,
            getArchive: id => Task.FromResult<ArchiveSnapshot?>(
                id == DailyRtId ? dailySnapshot :
                id == RawRtId ? rawSnapshot : null),
            getRollup: id => Task.FromResult<RollupArchiveSnapshot?>(
                id == DailyRtId ? daily : null),
            TestContext.Current.CancellationToken);

        // All four physical columns collapse to a single logical attribute path.
        Assert.Equal(new[] { "amountValue" }, result);
    }

    [Fact]
    public async Task BrokenChain_MissingParentArchive_DropsAffectedSpec()
    {
        var monthly = MakeRollup(MonthlyRtId, DailyRtId,
            new CkRollupAggregationSpec("amountvalue_sum", CkRollupFunction.Sum, null));

        var result = await RollupLogicalPathResolver.ResolveAsync(
            monthly,
            getArchive: _ => Task.FromResult<ArchiveSnapshot?>(null),
            getRollup: _ => Task.FromResult<RollupArchiveSnapshot?>(null),
            TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task DuplicateLogicalPaths_AreDeduplicated()
    {
        // Daily materialises AVG of voltage, which expands to (voltage_avg_sum, voltage_avg_count).
        // Monthly reads both — both should collapse to a single "voltage" entry.
        var daily = MakeRollup(DailyRtId, RawRtId,
            new CkRollupAggregationSpec("voltage", CkRollupFunction.Avg, null));

        var monthly = MakeRollup(MonthlyRtId, DailyRtId,
            new CkRollupAggregationSpec("voltage_avg_sum", CkRollupFunction.Sum, null),
            new CkRollupAggregationSpec("voltage_avg_count", CkRollupFunction.Sum, null));

        var dailySnapshot = ToArchiveSnapshot(daily);
        var rawSnapshot = MakeRawArchive(RawRtId);

        var result = await RollupLogicalPathResolver.ResolveAsync(
            monthly,
            getArchive: id => Task.FromResult<ArchiveSnapshot?>(
                id == DailyRtId ? dailySnapshot :
                id == RawRtId ? rawSnapshot : null),
            getRollup: id => Task.FromResult<RollupArchiveSnapshot?>(
                id == DailyRtId ? daily : null),
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "voltage" }, result);
    }

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

    /// <summary>
    /// Converts a rollup snapshot into the generic ArchiveSnapshot view the resolver sees from
    /// the archive store — the RollupAggregations slot is what tells the walker "this is a
    /// rollup, recurse via getRollup".
    /// </summary>
    private static ArchiveSnapshot ToArchiveSnapshot(RollupArchiveSnapshot rollup)
        => new(rollup.RtId, rollup.TargetCkTypeId, rollup.Status, rollup.RtWellKnownName, Array.Empty<CkArchiveColumnSpec>())
        {
            RollupAggregations = rollup.Aggregations
        };
}
