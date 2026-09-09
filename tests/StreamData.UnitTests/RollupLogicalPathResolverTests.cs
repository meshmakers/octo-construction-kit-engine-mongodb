using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Validates the chain walk that reverses physical storage column names back to logical CK
/// attribute paths for cascade rollups. Companion to <see cref="RollupQueryAggregationResolverTests"/>.
/// Since AB#5157 a rollup declares a <see cref="RollupArchiveSnapshot.Sources"/> list; the walker
/// climbs one parent per level, chosen by requested base → column discriminator → declaration order.
/// </summary>
public class RollupLogicalPathResolverTests
{
    private static readonly OctoObjectId RawRtId = new("aa00000000000000000001a0");
    private static readonly OctoObjectId DailyRtId = new("aa00000000000000000001b0");
    private static readonly OctoObjectId MonthlyRtId = new("aa00000000000000000001c0");
    private static readonly OctoObjectId LegacyRawRtId = new("aa00000000000000000001d0");
    private static readonly OctoObjectId OtherDailyRtId = new("aa00000000000000000001e0");
    private static readonly OctoObjectId UnrelatedRtId = new("aa00000000000000000001f0");
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

    // ---------- Multi-source rollups (AB#5157) ----------

    [Fact]
    public async Task TwoRawParents_ResolveToTheSameLogicalPaths()
    {
        // Legacy 15-min raw until the cutover, native raw from then on: both are raw archives of
        // the same CK type, so the spec's SourcePath is logical on either branch.
        var cutover = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var daily = MakeRollup(DailyRtId,
            new[]
            {
                new RollupSourceReference(LegacyRawRtId, ValidTo: cutover),
                new RollupSourceReference(RawRtId, ValidFrom: cutover),
            },
            new CkRollupAggregationSpec("amountValue", CkRollupFunction.Sum, null),
            new CkRollupAggregationSpec("voltage", CkRollupFunction.Avg, null));

        var result = await RollupLogicalPathResolver.ResolveAsync(
            daily,
            getArchive: id => Task.FromResult<ArchiveSnapshot?>(
                id == LegacyRawRtId ? MakeRawArchive(LegacyRawRtId) :
                id == RawRtId ? MakeRawArchive(RawRtId) : null),
            getRollup: _ => Task.FromResult<RollupArchiveSnapshot?>(null),
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "amountValue", "voltage" }, result);
    }

    [Fact]
    public async Task TwoRollupParents_OnlyTheParentMaterialisingTheColumnIsClimbed()
    {
        // Monthly lists two daily rollups: OtherDaily materialises voltage_sum (over raw voltage),
        // Daily materialises amountvalue_sum (over raw amountValue). Monthly's amountvalue_sum spec
        // must climb Daily — the first source in declaration order (OtherDaily) does NOT expose it.
        var daily = MakeRollup(DailyRtId, RawRtId,
            new CkRollupAggregationSpec("amountValue", CkRollupFunction.Sum, null));
        var otherDaily = MakeRollup(OtherDailyRtId, RawRtId,
            new CkRollupAggregationSpec("voltage", CkRollupFunction.Sum, null));
        var monthly = MakeRollup(MonthlyRtId,
            new[]
            {
                new RollupSourceReference(OtherDailyRtId, ValidTo: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                new RollupSourceReference(DailyRtId, ValidFrom: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            },
            new CkRollupAggregationSpec("amountvalue_sum", CkRollupFunction.Sum, null));

        var result = await RollupLogicalPathResolver.ResolveAsync(
            monthly,
            getArchive: id => Task.FromResult<ArchiveSnapshot?>(
                id == DailyRtId ? ToArchiveSnapshot(daily) :
                id == OtherDailyRtId ? ToArchiveSnapshot(otherDaily) :
                id == RawRtId ? MakeRawArchive(RawRtId) : null),
            getRollup: id => Task.FromResult<RollupArchiveSnapshot?>(
                id == DailyRtId ? daily :
                id == OtherDailyRtId ? otherDaily : null),
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "amountValue" }, result);
    }

    [Fact]
    public async Task RequestedBase_PicksTheOtherBranch()
    {
        // Both daily parents materialise amountvalue_sum, but over differently named raw paths
        // (a legacy import called the attribute "amount"). Declaration order would climb Daily
        // (→ "amountValue"); an explicit requestedBaseRtId = OtherDaily climbs the other branch.
        var daily = MakeRollup(DailyRtId, RawRtId,
            new CkRollupAggregationSpec("amountValue", CkRollupFunction.Sum, "amountvalue_sum"));
        var otherDaily = MakeRollup(OtherDailyRtId, LegacyRawRtId,
            new CkRollupAggregationSpec("amount", CkRollupFunction.Sum, "amountvalue_sum"));
        var monthly = MakeRollup(MonthlyRtId,
            new[]
            {
                new RollupSourceReference(DailyRtId, ValidFrom: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                new RollupSourceReference(OtherDailyRtId, ValidTo: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            },
            new CkRollupAggregationSpec("amountvalue_sum", CkRollupFunction.Sum, null));

        Task<ArchiveSnapshot?> GetArchive(OctoObjectId id) => Task.FromResult<ArchiveSnapshot?>(
            id == DailyRtId ? ToArchiveSnapshot(daily) :
            id == OtherDailyRtId ? ToArchiveSnapshot(otherDaily) :
            id == RawRtId ? MakeRawArchive(RawRtId) :
            id == LegacyRawRtId ? MakeRawArchive(LegacyRawRtId) : null);
        Task<RollupArchiveSnapshot?> GetRollup(OctoObjectId id) => Task.FromResult<RollupArchiveSnapshot?>(
            id == DailyRtId ? daily :
            id == OtherDailyRtId ? otherDaily : null);

        var byDeclarationOrder = await RollupLogicalPathResolver.ResolveAsync(
            monthly, GetArchive, GetRollup, TestContext.Current.CancellationToken);
        var byRequestedBase = await RollupLogicalPathResolver.ResolveAsync(
            monthly, GetArchive, GetRollup, TestContext.Current.CancellationToken, requestedBaseRtId: OtherDailyRtId);

        Assert.Equal(new[] { "amountValue" }, byDeclarationOrder);
        Assert.Equal(new[] { "amount" }, byRequestedBase);
    }

    [Fact]
    public async Task EmptySources_ResolvesNothing()
    {
        // A rollup without any declared source has nothing to climb — the spec is dropped, the
        // picker stays empty instead of throwing.
        var orphan = MakeRollup(DailyRtId, Array.Empty<RollupSourceReference>(),
            new CkRollupAggregationSpec("amountValue", CkRollupFunction.Sum, null));

        var result = await RollupLogicalPathResolver.ResolveAsync(
            orphan,
            getArchive: id => Task.FromResult<ArchiveSnapshot?>(MakeRawArchive(id)),
            getRollup: _ => Task.FromResult<RollupArchiveSnapshot?>(null),
            TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task RequestedBase_TwoLevelsUp_ResolvesThroughTheBranchThatReachesIt()
    {
        // Monthly → { Daily → Raw, OtherDaily → LegacyRaw }. Both dailies materialise the same
        // column, so the discriminator can't separate them; the requested base is a RAW archive
        // two levels up, reachable only through one of the branches.
        var (monthly, getArchive, getRollup) = MakeDiamond();

        var throughRaw = await RollupLogicalPathResolver.ResolveAsync(
            monthly, getArchive, getRollup, TestContext.Current.CancellationToken, requestedBaseRtId: RawRtId);
        var throughLegacyRaw = await RollupLogicalPathResolver.ResolveAsync(
            monthly, getArchive, getRollup, TestContext.Current.CancellationToken, requestedBaseRtId: LegacyRawRtId);

        Assert.Equal(new[] { "amountValue" }, throughRaw);
        Assert.Equal(new[] { "amount" }, throughLegacyRaw);
    }

    [Fact]
    public async Task RequestedBase_ReachableFromNoSource_FallsBackToTheDefaultSelection()
    {
        // An unrelated base is not on any branch — rule (1) must not apply, and declaration order
        // (rule 3, both branches materialise the column) decides as it did before AB#5157.
        var (monthly, getArchive, getRollup) = MakeDiamond();

        var result = await RollupLogicalPathResolver.ResolveAsync(
            monthly, getArchive, getRollup, TestContext.Current.CancellationToken, requestedBaseRtId: UnrelatedRtId);

        Assert.Equal(new[] { "amountValue" }, result);
    }

    [Fact]
    public async Task CyclicSourceGraph_WithRequestedBase_Terminates()
    {
        // Store inconsistency: Monthly declares Daily as source and Daily declares Monthly back.
        // Both the reachability probe and the chain walk must stop instead of spinning.
        var monthly = MakeRollup(MonthlyRtId, DailyRtId,
            new CkRollupAggregationSpec("amountvalue_sum", CkRollupFunction.Sum, "amountvalue_sum"));
        var daily = MakeRollup(DailyRtId, MonthlyRtId,
            new CkRollupAggregationSpec("amountvalue_sum", CkRollupFunction.Sum, "amountvalue_sum"));

        var result = await RollupLogicalPathResolver.ResolveAsync(
            monthly,
            getArchive: id => Task.FromResult<ArchiveSnapshot?>(
                id == MonthlyRtId ? ToArchiveSnapshot(monthly) :
                id == DailyRtId ? ToArchiveSnapshot(daily) : null),
            getRollup: id => Task.FromResult<RollupArchiveSnapshot?>(
                id == MonthlyRtId ? monthly :
                id == DailyRtId ? daily : null),
            TestContext.Current.CancellationToken,
            requestedBaseRtId: UnrelatedRtId);

        Assert.Empty(result);
    }

    /// <summary>
    /// Monthly over two daily rollups that materialise the same physical column over differently
    /// named raw attributes ("amountValue" via Raw, "amount" via LegacyRaw) — the shape where only
    /// the requested base can tell the branches apart.
    /// </summary>
    private static (
        RollupArchiveSnapshot Monthly,
        Func<OctoObjectId, Task<ArchiveSnapshot?>> GetArchive,
        Func<OctoObjectId, Task<RollupArchiveSnapshot?>> GetRollup) MakeDiamond()
    {
        var daily = MakeRollup(DailyRtId, RawRtId,
            new CkRollupAggregationSpec("amountValue", CkRollupFunction.Sum, "amountvalue_sum"));
        var otherDaily = MakeRollup(OtherDailyRtId, LegacyRawRtId,
            new CkRollupAggregationSpec("amount", CkRollupFunction.Sum, "amountvalue_sum"));
        var monthly = MakeRollup(MonthlyRtId,
            new[]
            {
                new RollupSourceReference(DailyRtId, ValidFrom: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                new RollupSourceReference(OtherDailyRtId, ValidTo: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            },
            new CkRollupAggregationSpec("amountvalue_sum", CkRollupFunction.Sum, null));

        return (
            monthly,
            id => Task.FromResult<ArchiveSnapshot?>(
                id == DailyRtId ? ToArchiveSnapshot(daily) :
                id == OtherDailyRtId ? ToArchiveSnapshot(otherDaily) :
                id == RawRtId ? MakeRawArchive(RawRtId) :
                id == LegacyRawRtId ? MakeRawArchive(LegacyRawRtId) : null),
            id => Task.FromResult<RollupArchiveSnapshot?>(
                id == DailyRtId ? daily :
                id == OtherDailyRtId ? otherDaily : null));
    }

    private static RollupArchiveSnapshot MakeRollup(
        OctoObjectId rtId,
        OctoObjectId sourceRtId,
        params CkRollupAggregationSpec[] aggregations)
        => MakeRollup(rtId, new[] { new RollupSourceReference(sourceRtId) }, aggregations);

    private static RollupArchiveSnapshot MakeRollup(
        OctoObjectId rtId,
        IReadOnlyList<RollupSourceReference> sources,
        params CkRollupAggregationSpec[] aggregations)
        => new(
            RtId: rtId,
            TargetCkTypeId: CkType,
            Status: CkArchiveStatus.Activated,
            RtWellKnownName: null,
            Sources: sources,
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
