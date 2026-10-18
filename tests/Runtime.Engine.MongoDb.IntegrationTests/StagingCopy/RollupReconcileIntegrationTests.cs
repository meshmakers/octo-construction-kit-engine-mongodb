using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Reconcile;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     AB#6968 against a real CrateDB: the rollup validator (one seeded inconsistency per check), the restore reconcile marker
///     and the repair. A rollup of 20 days of generation 0 plus four recomputed days (generations 1 to 4, each with its pointer
///     entry; the last one scoped to one entity) is the consistent baseline.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class RollupReconcileIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly DateTime Origin = CrateStagingCopyFixture.RollupOrigin;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Case
    {
        public required string Tenant { get; init; }
        public required OctoObjectId Rollup { get; init; }
        public required OctoObjectId Source { get; init; }
        public required RollupArchiveSnapshot Snapshot { get; init; }
        public required CrateDbStreamDataRepository Repository { get; init; }
        public string Live => TenantSchema.QualifiedArchiveTable(Tenant, Rollup.ToString());
        public string GenMap => GenerationMapSqlBuilder.GenMapTable(Tenant, Rollup.ToString());
    }

    private async Task<Case> NewCaseAsync(
        int generations = 4, DateTime? watermark = null, bool recomputeInProgress = false, int rawHours = 0)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rollup = OctoObjectId.GenerateNewId();
        var source = OctoObjectId.GenerateNewId();
        await fixture.CreateRollupAsync(tenant, rollup, generations);
        await fixture.CreateRawArchiveAsync(tenant, source, rawHours);
        var snapshot = new RollupArchiveSnapshot(rollup, new RtCkId<CkTypeId>("Test", new CkTypeId("Sensor")), CkArchiveStatus.Activated,
            "hourly", [new RollupSourceReference(source)], TimeSpan.FromHours(1), TimeSpan.Zero, watermark,
            [new CkRollupAggregationSpec("Value", CkRollupFunction.Sum, null)], null)
        {
            RecomputeInProgress = recomputeInProgress
        };
        return new Case
        {
            Tenant = tenant,
            Rollup = rollup,
            Source = source,
            Snapshot = snapshot,
            Repository = fixture.NewReconcileRepository(tenant, [snapshot], [source])
        };
    }

    private static async Task<IReadOnlyList<RollupConsistencyFinding>> FindingsAsync(Case c)
    {
        var report = await c.Repository.ValidateRollupsAsync(null, Ct);
        return report.Rollups.Single().Findings;
    }

    // ---- validator -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Validate_AConsistentRollup_ReportsNothing()
    {
        var c = await NewCaseAsync(watermark: Origin.AddHours(480), rawHours: 480);

        var report = await c.Repository.ValidateRollupsAsync(null, Ct);

        Assert.True(report.IsOk);
        Assert.Equal(c.Rollup, report.Rollups.Single().RollupRtId);
    }

    [Fact]
    public async Task Validate_A_ThePointerOfAGenerationWithoutRows_IsFound()
    {
        var c = await NewCaseAsync();
        await fixture.SqlAsync($"DELETE FROM {c.Live} WHERE \"generation\" = 3");
        await fixture.SqlAsync($"REFRESH TABLE {c.Live}");

        var finding = Assert.Single(await FindingsAsync(c));

        Assert.Equal(RollupConsistencyCheck.PointerWithoutRows, finding.Check);
        Assert.Equal(3, finding.Generation);
        Assert.Equal(Origin.AddHours(48), finding.RangeFromUtc); // day 3 of the baseline
        Assert.Equal(Origin.AddHours(72), finding.RangeToUtc);
    }

    [Fact]
    public async Task Validate_B_RowsAboveEveryPointer_AreFound_WithTheirCounts()
    {
        var c = await NewCaseAsync();
        // generation 9: no pointer at all; generation 2: outside the day its pointer covers
        await fixture.SqlAsync(
            $"INSERT INTO {c.Live} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"generation\", \"value\") " +
            "SELECT CAST(1767225600000 + CAST(generate_series AS BIGINT) * 3600000 AS TIMESTAMP WITH TIME ZONE), " +
            "CAST(1767225600000 + (CAST(generate_series AS BIGINT) + 1) * 3600000 AS TIMESTAMP WITH TIME ZONE), 'rt1', 'Test/Sensor-1', FALSE, 9, 1.0 " +
            "FROM generate_series(100, 109)");
        await fixture.SqlAsync(
            $"INSERT INTO {c.Live} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"generation\", \"value\") " +
            "SELECT CAST(1767225600000 + CAST(generate_series AS BIGINT) * 3600000 AS TIMESTAMP WITH TIME ZONE), " +
            "CAST(1767225600000 + (CAST(generate_series AS BIGINT) + 1) * 3600000 AS TIMESTAMP WITH TIME ZONE), 'rt1', 'Test/Sensor-1', FALSE, 2, 1.0 " +
            "FROM generate_series(300, 305)");
        await fixture.SqlAsync($"REFRESH TABLE {c.Live}");

        var findings = await FindingsAsync(c);

        Assert.All(findings, f => Assert.Equal(RollupConsistencyCheck.OrphansAbovePointer, f.Check));
        Assert.Equal(2, findings.Count);
        Assert.Equal(10, findings.Single(f => f.Generation == 9).Rows);
        Assert.Equal(6, findings.Single(f => f.Generation == 2).Rows);
    }

    [Fact]
    public async Task Validate_B_OrphansAreNotReported_WhileARecomputeRuns()
    {
        var c = await NewCaseAsync(recomputeInProgress: true);
        await fixture.SqlAsync(
            $"INSERT INTO {c.Live} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"generation\", \"value\") " +
            "VALUES (1767225600000, 1767229200000, 'rt9', 'Test/Sensor-1', FALSE, 9, 1.0)");
        await fixture.SqlAsync($"REFRESH TABLE {c.Live}");

        Assert.Empty(await FindingsAsync(c));
    }

    [Fact]
    public async Task Validate_C_AWatermarkAheadOfTheRows_IsFound_WithTheGapAndTheSourceRowsInBetween()
    {
        // the rollup rows end after 20 days, the watermark says 30 days, the source holds 30 days of hourly rows
        var c = await NewCaseAsync(watermark: Origin.AddDays(30), rawHours: 720);

        var finding = Assert.Single(await FindingsAsync(c));

        Assert.Equal(RollupConsistencyCheck.WatermarkAheadOfRows, finding.Check);
        Assert.Equal(TimeSpan.FromDays(10), finding.Gap);
        Assert.Equal(240, finding.Rows);
        Assert.Equal(Origin.AddDays(20), finding.RangeFromUtc);
        Assert.Contains(c.Rollup.ToString(), finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_C_AWatermarkAheadOfTheRows_IsNotAFinding_WhenTheSourceIsQuietThere()
    {
        var c = await NewCaseAsync(watermark: Origin.AddDays(30), rawHours: 480); // the source ends where the rollup ends

        Assert.Empty(await FindingsAsync(c));
    }

    [Fact]
    public async Task Validate_D_ARollupWithoutItsGenerationMapTable_IsFound()
    {
        var c = await NewCaseAsync(generations: 0);
        await fixture.SqlAsync($"DROP TABLE {c.GenMap}");

        var finding = Assert.Single(await FindingsAsync(c));

        Assert.Equal(RollupConsistencyCheck.MissingGenerationMap, finding.Check);
        Assert.Contains("__genmap", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_OnlyTheRequestedRollup_IsChecked()
    {
        var c = await NewCaseAsync();

        var report = await c.Repository.ValidateRollupsAsync([OctoObjectId.GenerateNewId()], Ct);

        Assert.Empty(report.Rollups);
    }

    // ---- marker --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Marker_IsAbsentByDefault_SetAndClearedDurably_AndSeenByAnotherProcess()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var one = fixture.NewReconcileRepository(tenant, [], [], new RecomputeProcessState());

        Assert.False(await one.IsRestoreReconcilePendingAsync(Ct)); // no table: not pending (tenants from before this feature)

        await one.SetRestoreReconcilePendingAsync(true, Ct);
        Assert.True(await one.IsRestoreReconcilePendingAsync(Ct));
        // another process: its own state and cache, only the CrateDB row is shared
        var other = fixture.NewReconcileRepository(tenant, [], [], new RecomputeProcessState());
        Assert.True(await other.IsRestoreReconcilePendingAsync(Ct));

        await one.SetRestoreReconcilePendingAsync(false, Ct);
        Assert.False(await one.IsRestoreReconcilePendingAsync(Ct));
        Assert.False(await fixture.NewReconcileRepository(tenant, [], [], new RecomputeProcessState()).IsRestoreReconcilePendingAsync(Ct));
    }

    [Fact]
    public async Task Marker_OfOneTenant_DoesNotMarkAnotherTenantOfTheSameSchema()
    {
        // 'acme-corp' and 'acme_corp' map to one CrateDB schema; the row is keyed by the full tenant id
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var a = fixture.NewReconcileRepository($"mk-{suffix}", [], [], new RecomputeProcessState());
        var b = fixture.NewReconcileRepository($"mk_{suffix}", [], [], new RecomputeProcessState());

        await a.SetRestoreReconcilePendingAsync(true, Ct);

        Assert.True(await a.IsRestoreReconcilePendingAsync(Ct));
        Assert.False(await b.IsRestoreReconcilePendingAsync(Ct));
    }

    [Fact]
    public async Task Marker_ClearingAMarkerThatWasNeverSet_IsANoOp()
    {
        var one = fixture.NewReconcileRepository(CrateStagingCopyFixture.NewTenantId(), [], [], new RecomputeProcessState());

        await one.SetRestoreReconcilePendingAsync(false, Ct);

        Assert.False(await one.IsRestoreReconcilePendingAsync(Ct));
    }
}
