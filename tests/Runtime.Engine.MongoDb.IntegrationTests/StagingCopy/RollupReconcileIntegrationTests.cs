using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;
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

    // ---- a rollup whose table is missing -------------------------------------------------------------------

    [Fact]
    public async Task ARollupEntityWithoutItsTable_IsNotChecked_TheReportIsNotOk_AndTheMarkerStaysSetAfterARepair()
    {
        // The state a restore leaves when the load of a rollup failed: the entity exists (status Disabled), the table does not.
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rollup = OctoObjectId.GenerateNewId();
        var source = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, source, 24);
        var snapshot = new RollupArchiveSnapshot(rollup, new RtCkId<CkTypeId>("Test", new CkTypeId("Sensor")), CkArchiveStatus.Disabled,
            "hourly", [new RollupSourceReference(source)], TimeSpan.FromHours(1), TimeSpan.Zero, null,
            [new CkRollupAggregationSpec("Value", CkRollupFunction.Sum, null)], null);
        var repository = fixture.NewReconcileRepository(tenant, [snapshot], [source]);
        await repository.SetRestoreReconcilePendingAsync(true, Ct);

        var report = await repository.ValidateRollupsAsync(null, Ct);

        var entry = report.Rollups.Single();
        Assert.Equal(RollupNotCheckedCause.NoLiveTable, entry.NotChecked!.Cause);
        Assert.False(report.IsOk);
        Assert.True(report.HasNotChecked);

        var repair = await repository.RepairRollupsAsync(null, dryRun: false, Ct);

        Assert.Empty(repair.Actions);
        Assert.False(repair.IsOk);
        Assert.Contains("could not be checked", repair.FailedStep, StringComparison.Ordinal);
        Assert.True(await repository.IsRestoreReconcilePendingAsync(Ct));
        // The repair did not turn the missing table into an empty one.
        Assert.Equal(0, await fixture.CountAsync(
            $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{TenantSchema.ArchiveTableName(rollup.ToString())}'"));
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

    // ---- repair --------------------------------------------------------------------------------------------

    /// <summary>
    ///     A rollup built the way the platform builds it: 48 hourly source rows, one bucket each aggregated forward at
    ///     generation 0 through the repository, the watermark at the end. The "unrestored reference" the repair is
    ///     compared with is what a reader sees of it.
    /// </summary>
    private sealed class RepairRig
    {
        private readonly CrateStagingCopyFixture _fixture;

        private RepairRig(CrateStagingCopyFixture fixture) => _fixture = fixture;

        public string Tenant { get; private init; } = null!;
        public OctoObjectId Raw { get; private init; }
        public OctoObjectId Rollup { get; private init; }
        public ArchiveSnapshot RawSnapshot { get; private init; } = null!;
        public RollupArchiveSnapshot Snapshot { get; private init; } = null!;
        public InMemoryRollupStore Store { get; private init; } = null!;
        public CrateDbStreamDataRepository Repository { get; private set; } = null!;
        public RecomputeProcessState State { get; } = new();
        public string Live => TenantSchema.QualifiedArchiveTable(Tenant, Rollup.ToString());
        public string GenMap => GenerationMapSqlBuilder.GenMapTable(Tenant, Rollup.ToString());

        public static async Task<RepairRig> CreateAsync(
            CrateStagingCopyFixture fixture, int rawHours = 48, IStreamDataDatabaseClient? databaseClient = null)
        {
            var tenant = CrateStagingCopyFixture.NewTenantId();
            var raw = OctoObjectId.GenerateNewId();
            var rollup = OctoObjectId.GenerateNewId();
            await fixture.CreateRawArchiveAsync(tenant, raw, rawHours);
            var type = new RtCkId<CkTypeId>("Test", new CkTypeId("Sensor"));
            var specs = new[] { new CkRollupAggregationSpec("Value", CkRollupFunction.Sum, null) };
            var snapshot = new RollupArchiveSnapshot(rollup, type, CkArchiveStatus.Activated, "hourly", [new RollupSourceReference(raw)],
                TimeSpan.FromHours(1), TimeSpan.Zero, Origin.AddHours(rawHours), specs, null);
            var rig = new RepairRig(fixture)
            {
                Tenant = tenant,
                Raw = raw,
                Rollup = rollup,
                Snapshot = snapshot,
                RawSnapshot = new ArchiveSnapshot(raw, type, CkArchiveStatus.Activated, "raw", [new CkArchiveColumnSpec("Value", true, false)]),
                Store = new InMemoryRollupStore(snapshot)
            };
            rig.Repository = fixture.NewReconcileRepository(tenant, [snapshot], [raw], rig.State, null, rig.Store, databaseClient);
            await rig.Repository.EnsureArchiveCreatedAsync(new ArchiveSnapshot(rollup, type, CkArchiveStatus.Activated, "hourly",
                RollupColumnGenerator.Generate(specs)) { RollupAggregations = specs, Period = TimeSpan.FromHours(1) });
            await rig.AggregateForwardAsync(0, rawHours);
            return rig;
        }

        /// <summary>The forward aggregation of the buckets [fromHour, toHour), as the orchestrator does it tick by tick.</summary>
        public async Task AggregateForwardAsync(int fromHour, int toHour)
        {
            for (var h = fromHour; h < toHour; h++)
            {
                await Repository.AggregateBucketAsync(RawSnapshot, Store.GetAsync(Rollup).Result!, Origin.AddHours(h), Origin.AddHours(h + 1), Ct);
            }

            await _fixture.SqlAsync($"REFRESH TABLE {Live}");
        }

        /// <summary>A recompute of [fromHour, toHour) through the repository, with the immediate sweep of a repository without options.</summary>
        public async Task RecomputeAsync(int fromHour, int toHour)
        {
            await Repository.ExecuteAsync(RawSnapshot, Store.GetAsync(Rollup).Result!, Origin.AddHours(fromHour), Origin.AddHours(toHour), null, Ct);
        }

        public async Task SqlAsync(string sql)
        {
            await _fixture.SqlAsync(sql);
            await _fixture.SqlAsync($"REFRESH TABLE {Live}");
            await _fixture.SqlAsync($"REFRESH TABLE {GenMap}");
        }

        public async Task<long> CountAsync(string where) => await _fixture.CountAsync($"SELECT COUNT(*) FROM {Live} WHERE {where}");

        public async Task<List<string>> PointersAsync()
        {
            await _fixture.SqlAsync($"REFRESH TABLE {GenMap}");
            return (await _fixture.QueryAsync($"SELECT \"range_start\", \"range_end\", \"rtid_scope\", \"generation\" FROM {GenMap} ORDER BY 1, 2, 3, 4"))
                .Select(r => string.Join("|", r.Values)).ToList();
        }

        /// <summary>What a reader sees: window and value of the rows the pointer selects (the read predicate of the query path).</summary>
        public async Task<List<string>> ActiveViewAsync()
        {
            await _fixture.SqlAsync($"REFRESH TABLE {Live}");
            await _fixture.SqlAsync($"REFRESH TABLE {GenMap}");
            var ranges = (await _fixture.QueryAsync($"SELECT \"range_start\", \"range_end\", \"rtid_scope\", \"generation\" FROM {GenMap}"))
                .Select(r => new GenerationRange(Convert.ToInt64(r["range_start"]), Convert.ToInt64(r["range_end"]),
                    Convert.ToString(r["rtid_scope"]) ?? string.Empty, Convert.ToInt64(r["generation"])))
                .ToList();
            var predicate = GenerationFilterSql.Render(ranges);
            return (await _fixture.QueryAsync($"SELECT \"window_start\", \"value_sum\" FROM {Live} WHERE {predicate} ORDER BY 1"))
                .Select(r => string.Join("|", Convert.ToDateTime(r["window_start"]).ToUniversalTime().ToString("O"), r["value_sum"])).ToList();
        }
    }

    [Fact]
    public async Task Repair_WatermarkAheadOfTheRows_RewindsToTheEndOfTheData_AndTheForwardAggregationRestoresTheReference()
    {
        var rig = await RepairRig.CreateAsync(fixture);
        var reference = await rig.ActiveViewAsync();
        Assert.Equal(48, reference.Count);
        await rig.SqlAsync($"DELETE FROM {rig.Live} WHERE \"window_start\" >= 1767225600000 + 24 * 3600000"); // the restored rollup ends after 24 hours

        var report = await rig.Repository.RepairRollupsAsync(null, false, Ct);

        var action = Assert.Single(report.Actions);
        Assert.Equal(RollupRepairKind.RewindWatermark, action.Kind);
        Assert.Equal(RollupRepairStatus.Done, action.Status);
        Assert.Equal((rig.Rollup, Origin.AddHours(24), true), rig.Store.WatermarkWrites.Single());
        Assert.False(report.Before.IsOk);
        Assert.True(report.After!.IsOk);
        Assert.True(report.IsOk);
        // the forward aggregation, from the rewound watermark on, fills the gap: the values are the reference's
        await rig.AggregateForwardAsync(24, 48);
        Assert.Equal(reference, await rig.ActiveViewAsync());
    }

    [Fact]
    public async Task Repair_PointerWithoutRows_RecomputesTheRange_ValuesEqualTheReference_AndNoWindowStaysEmpty()
    {
        var rig = await RepairRig.CreateAsync(fixture);
        await rig.RecomputeAsync(0, 24); // generation 1 over day 1; the sweep removed the generation 0 rows there
        var reference = await rig.ActiveViewAsync();
        await rig.SqlAsync($"DELETE FROM {rig.Live} WHERE \"generation\" = 1"); // the restore lost the rows, the pointer stayed
        Assert.Equal(24, (await rig.ActiveViewAsync()).Count); // day 2 only: day 1 is empty

        var report = await rig.Repository.RepairRollupsAsync(null, false, Ct);

        Assert.True(report.IsOk);
        Assert.Equal(RollupConsistencyCheck.PointerWithoutRows, report.Before.Rollups.Single().Findings.Single().Check);
        Assert.Contains(report.Actions, a => a is { Kind: RollupRepairKind.RecomputePointerRange, Status: RollupRepairStatus.Done });
        Assert.Equal(reference, await rig.ActiveViewAsync());
        Assert.Equal(48, reference.Count);
        // the old entry is gone, the recompute's entry names rows that exist
        Assert.DoesNotContain(await rig.PointersAsync(), p => p.EndsWith("|1", StringComparison.Ordinal));
        Assert.True(report.After!.IsOk);
    }

    [Fact]
    public async Task Repair_AHoleInTheMiddle_RecomputesOnlyThatRange_AndKeepsTheRowsRecomputedLater()
    {
        var rig = await RepairRig.CreateAsync(fixture);
        await rig.RecomputeAsync(0, 48);   // generation 1 over both days
        await rig.RecomputeAsync(10, 20);  // generation 2 over hours 10..20, recomputed later
        var reference = await rig.ActiveViewAsync();
        var laterRows = await rig.CountAsync("\"generation\" = 2");
        var laterSum = await fixture.CountAsync($"SELECT CAST(SUM(\"value_sum\") AS BIGINT) FROM {rig.Live} WHERE \"generation\" = 2");
        await rig.SqlAsync($"DELETE FROM {rig.Live} WHERE \"generation\" = 1"); // the rows of generation 1 are lost

        var report = await rig.Repository.RepairRollupsAsync(null, false, Ct);

        Assert.True(report.IsOk);
        var recomputed = report.Actions.Where(a => a is { Kind: RollupRepairKind.RecomputePointerRange, RangeFromUtc: not null, Status: RollupRepairStatus.Done }).ToList();
        // two holes, and the rows lost behind hour 20 also leave the watermark ahead of the rollup data
        Assert.Equal(2, report.Before.Rollups.Single().Findings.Count(f => f.Check == RollupConsistencyCheck.PointerWithoutRows));
        Assert.Single(report.Before.Rollups.Single().Findings, f => f.Check == RollupConsistencyCheck.WatermarkAheadOfRows);
        // ... but the recompute of the holes filled the rollup up to the watermark again, so the watermark is left alone
        Assert.Empty(rig.Store.WatermarkWrites);
        Assert.Contains(recomputed, a => a.RangeFromUtc == Origin && a.RangeToUtc == Origin.AddHours(10));
        Assert.Contains(recomputed, a => a.RangeFromUtc == Origin.AddHours(20) && a.RangeToUtc == Origin.AddHours(48));
        Assert.DoesNotContain(recomputed, a => a.RangeFromUtc < Origin.AddHours(20) && a.RangeToUtc > Origin.AddHours(10)
                                               && a.RangeFromUtc != Origin); // the later range is not recomputed again
        Assert.Equal(laterRows, await rig.CountAsync("\"generation\" = 2")); // not deleted
        Assert.Equal(laterSum, await fixture.CountAsync($"SELECT CAST(SUM(\"value_sum\") AS BIGINT) FROM {rig.Live} WHERE \"generation\" = 2"));
        Assert.Equal(reference, await rig.ActiveViewAsync());
    }

    [Fact]
    public async Task Repair_Orphans_AreDeleted_AndThePointedGenerationIsUntouched()
    {
        var rig = await RepairRig.CreateAsync(fixture);
        await rig.RecomputeAsync(0, 24); // generation 1
        var reference = await rig.ActiveViewAsync();
        var pointedRows = await rig.CountAsync("\"generation\" = 1");
        await rig.SqlAsync(
            $"INSERT INTO {rig.Live} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"generation\", \"value_sum\") " +
            "SELECT CAST(1767225600000 + CAST(generate_series AS BIGINT) * 3600000 AS TIMESTAMP WITH TIME ZONE), " +
            "CAST(1767225600000 + (CAST(generate_series AS BIGINT) + 1) * 3600000 AS TIMESTAMP WITH TIME ZONE), 'rt1', 'Test/Sensor-1', FALSE, 9, 99.0 " +
            "FROM generate_series(30, 39)");
        Assert.Equal(10, await rig.CountAsync("\"generation\" = 9"));

        var report = await rig.Repository.RepairRollupsAsync(null, false, Ct);

        Assert.True(report.IsOk);
        Assert.Contains(report.Before.Rollups.Single().Findings, f => f.Check == RollupConsistencyCheck.OrphansAbovePointer && f.Generation == 9 && f.Rows == 10);
        Assert.Equal(0, await rig.CountAsync("\"generation\" = 9"));
        Assert.Equal(pointedRows, await rig.CountAsync("\"generation\" = 1"));
        Assert.Equal(reference, await rig.ActiveViewAsync());
    }

    [Fact]
    public async Task Repair_AMissingGenerationMap_IsCreated()
    {
        var rig = await RepairRig.CreateAsync(fixture);
        var reference = await rig.ActiveViewAsync();
        await fixture.SqlAsync($"DROP TABLE {rig.GenMap}");

        var report = await rig.Repository.RepairRollupsAsync(null, false, Ct);

        Assert.True(report.IsOk);
        Assert.Contains(report.Actions, a => a is { Kind: RollupRepairKind.CreateGenerationMap, Status: RollupRepairStatus.Done });
        Assert.Equal(reference, await rig.ActiveViewAsync());
    }

    [Fact]
    public async Task Repair_ALostMapWithRecomputedRows_RecomputesTheWholeRollup_AndEndsOkInOneRun()
    {
        var rig = await RepairRig.CreateAsync(fixture);
        await rig.RecomputeAsync(0, 24);   // generation 1 over day 1 (the sweep removed the generation-0 rows there)
        await rig.RecomputeAsync(10, 20);  // generation 2
        var reference = await rig.ActiveViewAsync();
        await fixture.SqlAsync($"DROP TABLE {rig.GenMap}");
        await rig.Repository.SetRestoreReconcilePendingAsync(true, Ct);

        var report = await rig.Repository.RepairRollupsAsync(null, false, Ct);

        Assert.True(report.IsOk, report.FailedStep);
        Assert.Null(report.FailedStep);
        Assert.True(report.After!.IsOk);
        Assert.Equal(RollupRepairKind.CreateGenerationMap, report.Actions[0].Kind);
        Assert.DoesNotContain(report.Actions, a => a.Kind == RollupRepairKind.DeleteOrphans); // nothing is deleted blindly
        Assert.Equal(reference, await rig.ActiveViewAsync()); // every window is back, with the source's values
        Assert.False(await rig.Repository.IsRestoreReconcilePendingAsync(Ct));
    }

    [Fact]
    public async Task Repair_NeverEndsInconsistentWithoutNamingWhatIsWrong()
    {
        var rig = await RepairRig.CreateAsync(fixture);
        // a defect the repair cannot fix: the watermark is ahead and the source has rows, but the store ignores the rewind
        await rig.SqlAsync($"DELETE FROM {rig.Live} WHERE \"window_start\" >= 1767225600000 + 24 * 3600000");
        // the validator still sees the gap after the 'rewind', because the store ignores the write
        var ignoring = A.Fake<IRollupArchiveRuntimeStore>(o => o.Wrapping(rig.Store));
        A.CallTo(() => ignoring.AdvanceWatermarkAsync(A<OctoObjectId>._, A<DateTime>._, A<bool>._)).Returns(Task.CompletedTask);
        var stubborn = fixture.NewReconcileRepository(rig.Tenant, [rig.Snapshot], [rig.Raw], new RecomputeProcessState(), null, ignoring);

        var report = await stubborn.RepairRollupsAsync(null, false, Ct);

        Assert.False(report.IsOk);
        Assert.False(string.IsNullOrEmpty(report.FailedStep)); // always names the finding that is left
        Assert.Contains("WatermarkAheadOfRows", report.FailedStep, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repair_APointerWhoseSourceHoldsNothing_IsRemoved_SoTheValidatorIsOk()
    {
        var rig = await RepairRig.CreateAsync(fixture, rawHours: 24);
        // a pointer into a range the source does not cover (hours 30..40), with no rows behind it
        await rig.SqlAsync(GenerationMapSqlBuilder.BuildUpsertPointer(rig.GenMap, Origin.AddHours(30), Origin.AddHours(40), "", 7));
        Assert.False((await rig.Repository.ValidateRollupsAsync(null, Ct)).IsOk);

        var report = await rig.Repository.RepairRollupsAsync(null, false, Ct);

        Assert.True(report.IsOk);
        Assert.DoesNotContain(await rig.PointersAsync(), p => p.Contains("|7", StringComparison.Ordinal));
        Assert.True((await rig.Repository.ValidateRollupsAsync(null, Ct)).IsOk);
    }

    [Fact]
    public async Task Repair_DryRun_ListsThePlannedSteps_AndChangesNothing()
    {
        var rig = await RepairRig.CreateAsync(fixture);
        await rig.RecomputeAsync(0, 48);
        await rig.RecomputeAsync(10, 20);
        await rig.SqlAsync($"DELETE FROM {rig.Live} WHERE \"generation\" = 1");
        await rig.SqlAsync($"DELETE FROM {rig.Live} WHERE \"window_start\" >= 1767225600000 + 40 * 3600000");
        await rig.Repository.SetRestoreReconcilePendingAsync(true, Ct);
        var viewBefore = await rig.ActiveViewAsync();
        var pointersBefore = await rig.PointersAsync();
        var rowsBefore = await rig.CountAsync("1 = 1");

        var report = await rig.Repository.RepairRollupsAsync(null, dryRun: true, Ct);

        Assert.True(report.DryRun);
        Assert.Null(report.After);
        Assert.NotEmpty(report.Actions);
        Assert.All(report.Actions, a => Assert.Equal(RollupRepairStatus.Planned, a.Status));
        Assert.Contains(report.Actions, a => a.Kind == RollupRepairKind.RecomputePointerRange);
        Assert.Contains(report.Actions, a => a.Kind == RollupRepairKind.RewindWatermark);
        Assert.Equal(viewBefore, await rig.ActiveViewAsync());
        Assert.Equal(pointersBefore, await rig.PointersAsync());
        Assert.Equal(rowsBefore, await rig.CountAsync("1 = 1"));
        Assert.Empty(rig.Store.WatermarkWrites);
        Assert.True(await rig.Repository.IsRestoreReconcilePendingAsync(Ct)); // a dry run does not clear the marker
    }

    [Fact]
    public async Task Repair_IsIdempotent_ASecondRunFindsNothingToDo()
    {
        var rig = await RepairRig.CreateAsync(fixture);
        await rig.RecomputeAsync(0, 48);
        await rig.RecomputeAsync(10, 20);
        await rig.SqlAsync($"DELETE FROM {rig.Live} WHERE \"generation\" = 1");
        var first = await rig.Repository.RepairRollupsAsync(null, false, Ct);
        var viewAfterFirst = await rig.ActiveViewAsync();
        var pointersAfterFirst = await rig.PointersAsync();

        var second = await rig.Repository.RepairRollupsAsync(null, false, Ct);

        Assert.True(first.IsOk);
        Assert.NotEmpty(first.Actions);
        Assert.True(second.IsOk);
        Assert.Empty(second.Actions);
        Assert.True(second.Before.IsOk);
        Assert.Equal(viewAfterFirst, await rig.ActiveViewAsync());
        Assert.Equal(pointersAfterFirst, await rig.PointersAsync());
    }

    [Fact]
    public async Task Repair_ClearsTheMarker_OnlyWhenTheTenantValidatesOkAfterwards()
    {
        var rig = await RepairRig.CreateAsync(fixture);
        await rig.Repository.SetRestoreReconcilePendingAsync(true, Ct);

        var consistent = await rig.Repository.RepairRollupsAsync(null, false, Ct);

        Assert.True(consistent.IsOk);
        Assert.Empty(consistent.Actions);
        Assert.False(await rig.Repository.IsRestoreReconcilePendingAsync(Ct));
    }

    [Fact]
    public async Task Repair_WhenAStepFails_TheMarkerStays_TheFailingStepIsNamed_AndTheRestOfTheRollupIsSkipped()
    {
        var broken = A.Fake<IStreamDataDatabaseClient>(o => o.Wrapping(fixture.Client));
        A.CallTo(() => broken.ExecuteNonQueryAsync(A<string>._, A<string>.That.Contains("\"generation\" = 9"),
                A<CrateStatementClass>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("CrateDB is unavailable"));
        var rig = await RepairRig.CreateAsync(fixture, databaseClient: broken);
        await rig.RecomputeAsync(0, 48);
        await rig.RecomputeAsync(10, 20);
        await rig.SqlAsync($"DELETE FROM {rig.Live} WHERE \"generation\" = 1"); // a hole: a later step of the same rollup
        await rig.SqlAsync(
            $"INSERT INTO {rig.Live} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"generation\", \"value_sum\") " +
            "VALUES (1767225600000, 1767229200000, 'rt7', 'Test/Sensor-1', FALSE, 9, 1.0)");
        await rig.Repository.SetRestoreReconcilePendingAsync(true, Ct);
        var pointersBefore = await rig.PointersAsync();

        var report = await rig.Repository.RepairRollupsAsync(null, false, Ct);

        Assert.False(report.IsOk);
        Assert.NotNull(report.FailedStep);
        Assert.Contains(rig.Rollup.ToString(), report.FailedStep, StringComparison.Ordinal);
        Assert.Contains("generation 9", report.FailedStep, StringComparison.Ordinal);
        Assert.Contains("CrateDB is unavailable", report.FailedStep, StringComparison.Ordinal);
        Assert.Equal(RollupRepairKind.DeleteOrphans, report.Actions[0].Kind);
        Assert.Equal(RollupRepairStatus.Failed, report.Actions[0].Status);
        Assert.All(report.Actions.Skip(1), a => Assert.Equal(RollupRepairStatus.Skipped, a.Status));
        Assert.True(await rig.Repository.IsRestoreReconcilePendingAsync(Ct)); // the marker stays
        Assert.Equal(pointersBefore, await rig.PointersAsync()); // nothing after the failing step ran

        // CrateDB is back: the same repair on a healthy repository finishes the job and clears the marker
        var healthy = fixture.NewReconcileRepository(rig.Tenant, [rig.Snapshot], [rig.Raw], new RecomputeProcessState(), null, rig.Store);
        var second = await healthy.RepairRollupsAsync(null, false, Ct);
        Assert.True(second.IsOk);
        Assert.False(await healthy.IsRestoreReconcilePendingAsync(Ct));
    }

    // ---- queued repair -------------------------------------------------------------------------------------

    private static async Task<RepairRig> BrokenRigAsync(CrateStagingCopyFixture fixture, IStreamDataDatabaseClient? db = null)
    {
        var rig = await RepairRig.CreateAsync(fixture, databaseClient: db);
        await rig.RecomputeAsync(0, 48);
        await rig.RecomputeAsync(10, 20);
        await rig.SqlAsync($"DELETE FROM {rig.Live} WHERE \"generation\" = 1"); // holes: the repair has recomputes to do
        return rig;
    }

    [Fact]
    public async Task Enqueue_RecordsThePlan_ReturnsAtOnce_AndTheTickRunsItAfterARestart_ClearingTheMarker()
    {
        var rig = await BrokenRigAsync(fixture);
        var reference = new List<string>();
        await rig.Repository.SetRestoreReconcilePendingAsync(true, Ct);
        var before = await rig.ActiveViewAsync();

        var request = await rig.Repository.EnqueueRollupRepairAsync(null, Ct);

        Assert.Equal(RollupRepairRequestState.Pending, request.State);
        Assert.NotEmpty(request.PlannedActions);
        Assert.All(request.PlannedActions, a => Assert.Equal(RollupRepairStatus.Planned, a.Status));
        Assert.Null(request.Report);
        Assert.Equal(before, await rig.ActiveViewAsync()); // nothing was repaired by the call
        Assert.True(await rig.Repository.IsRestoreReconcilePendingAsync(Ct));

        // a restarted process (empty state) finds the durable request and runs it
        var restarted = fixture.NewReconcileRepository(rig.Tenant, [rig.Snapshot], [rig.Raw], new RecomputeProcessState(), null, rig.Store);
        var stored = await restarted.GetRollupRepairRequestAsync(request.RequestId, Ct);
        Assert.Equal(RollupRepairRequestState.Pending, stored!.State);
        Assert.Equal(1, await restarted.RunPendingRollupRepairsAsync(Ct));

        var done = await restarted.GetRollupRepairRequestAsync(request.RequestId, Ct);
        Assert.Equal(RollupRepairRequestState.Done, done!.State);
        Assert.NotNull(done.FinishedAtUtc);
        Assert.True(done.Report!.IsOk);
        Assert.Contains(done.Report.Actions, a => a is { Kind: RollupRepairKind.RecomputePointerRange, Status: RollupRepairStatus.Done });
        Assert.False(await restarted.IsRestoreReconcilePendingAsync(Ct)); // the repair released the tenant
        Assert.True((await restarted.ValidateRollupsAsync(null, Ct)).IsOk);
        Assert.Equal(0, await restarted.RunPendingRollupRepairsAsync(Ct)); // nothing runs twice
        Assert.Empty(reference);
    }

    [Fact]
    public async Task Enqueue_AFailingStep_EndsTheRequestFailed_NamesTheStep_AndKeepsTheMarker()
    {
        var broken = A.Fake<IStreamDataDatabaseClient>(o => o.Wrapping(fixture.Client));
        A.CallTo(() => broken.ExecuteNonQueryAsync(A<string>._, A<string>.That.Contains("\"generation\" = 9"),
                A<CrateStatementClass>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("CrateDB is unavailable"));
        var rig = await BrokenRigAsync(fixture, broken);
        await rig.SqlAsync(
            $"INSERT INTO {rig.Live} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"generation\", \"value_sum\") " +
            "VALUES (1767225600000, 1767229200000, 'rt7', 'Test/Sensor-1', FALSE, 9, 1.0)");
        await rig.Repository.SetRestoreReconcilePendingAsync(true, Ct);
        var request = await rig.Repository.EnqueueRollupRepairAsync(null, Ct);

        await rig.Repository.RunPendingRollupRepairsAsync(Ct);

        var ended = await rig.Repository.GetRollupRepairRequestAsync(request.RequestId, Ct);
        Assert.Equal(RollupRepairRequestState.Failed, ended!.State);
        Assert.Contains("generation 9", ended.Report!.FailedStep, StringComparison.Ordinal);
        Assert.True(await rig.Repository.IsRestoreReconcilePendingAsync(Ct));
        Assert.Equal(0, await rig.Repository.RunPendingRollupRepairsAsync(Ct)); // a failed request is not run again by itself
    }

    [Fact]
    public async Task Enqueue_ARequestLeftRunningByADeadProcess_IsClaimedAgainAfterTheTimeout()
    {
        var rig = await BrokenRigAsync(fixture);
        var request = await rig.Repository.EnqueueRollupRepairAsync(null, Ct);
        var table = $"\"{TenantSchema.SchemaName(rig.Tenant)}\".\"__rollup_repair\"";
        await fixture.SqlAsync($"UPDATE {table} SET \"state\" = 'Running', \"started_at_ms\" = {DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds()} " +
                               $"WHERE \"request_id\" = '{request.RequestId}'");
        await fixture.SqlAsync($"REFRESH TABLE {table}");

        Assert.Equal(1, await rig.Repository.RunPendingRollupRepairsAsync(Ct));
        Assert.Equal(RollupRepairRequestState.Done, (await rig.Repository.GetRollupRepairRequestAsync(request.RequestId, Ct))!.State);
    }

    [Fact]
    public async Task Enqueue_ASecondCallWhileOneIsActive_ReturnsTheExistingRequest_ADifferentRollupSetGetsItsOwn()
    {
        var rig = await BrokenRigAsync(fixture);

        var first = await rig.Repository.EnqueueRollupRepairAsync(null, Ct);
        var second = await rig.Repository.EnqueueRollupRepairAsync(null, Ct);
        var other = await rig.Repository.EnqueueRollupRepairAsync([rig.Rollup], Ct);

        Assert.Equal(first.RequestId, second.RequestId);
        Assert.NotEqual(first.RequestId, other.RequestId);

        // once the first has ended, the same call queues a new request
        await rig.Repository.RunPendingRollupRepairsAsync(Ct);
        var third = await rig.Repository.EnqueueRollupRepairAsync(null, Ct);
        Assert.NotEqual(first.RequestId, third.RequestId);
    }

    [Fact]
    public async Task Enqueue_OverlappingRequests_NeverRunAtOnce_TheSecondWaitsForTheFirst()
    {
        var rig = await BrokenRigAsync(fixture);
        var all = await rig.Repository.EnqueueRollupRepairAsync(null, Ct);
        var one = await rig.Repository.EnqueueRollupRepairAsync([rig.Rollup], Ct); // overlaps: the rollup is part of "all"
        var table = $"\"{TenantSchema.SchemaName(rig.Tenant)}\".\"__rollup_repair\"";

        // while the first is running (lease valid) the second is not claimed, not even by another replica
        await fixture.SqlAsync($"UPDATE {table} SET \"state\" = 'Running', \"started_at_ms\" = {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} WHERE \"request_id\" = '{all.RequestId}'");
        await fixture.SqlAsync($"REFRESH TABLE {table}");
        var other = fixture.NewReconcileRepository(rig.Tenant, [rig.Snapshot], [rig.Raw], new RecomputeProcessState(), null, rig.Store);
        Assert.Equal(0, await other.RunPendingRollupRepairsAsync(Ct));
        Assert.Equal(RollupRepairRequestState.Pending, (await other.GetRollupRepairRequestAsync(one.RequestId, Ct))!.State);

        // the first ends: the second runs on the next tick, one request per tick
        await fixture.SqlAsync($"UPDATE {table} SET \"state\" = 'Done', \"finished_at_ms\" = 1 WHERE \"request_id\" = '{all.RequestId}'");
        await fixture.SqlAsync($"REFRESH TABLE {table}");
        Assert.Equal(1, await other.RunPendingRollupRepairsAsync(Ct));
        Assert.Equal(RollupRepairRequestState.Done, (await other.GetRollupRepairRequestAsync(one.RequestId, Ct))!.State);
    }

    [Fact]
    public async Task Enqueue_TwoReplicasClaimingTheSameRequest_ExactlyOneWins()
    {
        var rig = await BrokenRigAsync(fixture);
        var request = await rig.Repository.EnqueueRollupRepairAsync(null, Ct);
        var table = $"\"{TenantSchema.SchemaName(rig.Tenant)}\".\"__rollup_repair\"";
        var row = (await fixture.QueryAsync($"SELECT \"_seq_no\" AS s, \"_primary_term\" AS p FROM {table} WHERE \"request_id\" = '{request.RequestId}'")).Single();
        var claim = RollupRepairQueue.BuildClaim(table, rig.Tenant, request.RequestId, Convert.ToInt64(row["s"]), Convert.ToInt64(row["p"]), 1);

        // both replicas read the same version and send the claim at the same moment
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Client.ExecuteNonQueryAsync(rig.Tenant, claim, CrateStatementClass.Maintenance, Ct)));

        Assert.Equal(1, results.Sum());
    }

    [Fact]
    public async Task Enqueue_ARunningRequest_IsNotClaimedAgain_AndAnUnknownIdIsNull()
    {
        var rig = await BrokenRigAsync(fixture);
        var request = await rig.Repository.EnqueueRollupRepairAsync(null, Ct);
        var table = $"\"{TenantSchema.SchemaName(rig.Tenant)}\".\"__rollup_repair\"";
        await fixture.SqlAsync($"UPDATE {table} SET \"state\" = 'Running', \"started_at_ms\" = {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} " +
                               $"WHERE \"request_id\" = '{request.RequestId}'");
        await fixture.SqlAsync($"REFRESH TABLE {table}");

        Assert.Equal(0, await rig.Repository.RunPendingRollupRepairsAsync(Ct));
        Assert.Null(await rig.Repository.GetRollupRepairRequestAsync("does-not-exist", Ct));
        Assert.Null(await fixture.NewReconcileRepository(CrateStagingCopyFixture.NewTenantId(), [], []).GetRollupRepairRequestAsync("x", Ct));
    }

    // ---- gate ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Gate_WhileTheTenantIsMarked_ForwardAggregationRecomputeAndSweepDoNotRun_AndRunAgainWhenItIsReleased()
    {
        var rig = await RepairRig.CreateAsync(fixture, rawHours: 24);
        await rig.Repository.SetRestoreReconcilePendingAsync(true, Ct);
        var rowsBefore = await rig.CountAsync("1 = 1");

        var aggregate = await Assert.ThrowsAsync<RestoreReconcilePendingException>(() => rig.AggregateForwardAsync(0, 1));
        var recompute = await Assert.ThrowsAsync<RestoreReconcilePendingException>(() => rig.RecomputeAsync(0, 12));

        Assert.Contains("forward aggregation", aggregate.Message, StringComparison.Ordinal);
        Assert.Contains("recompute", recompute.Message, StringComparison.Ordinal);
        Assert.Contains(rig.Rollup.ToString(), recompute.Message, StringComparison.Ordinal);
        Assert.Equal(0, await rig.Repository.SweepSupersededGenerationsAsync(Ct)); // nothing is swept
        Assert.Equal(rowsBefore, await rig.CountAsync("1 = 1")); // and nothing was written

        await rig.Repository.SetRestoreReconcilePendingAsync(false, Ct);

        await rig.AggregateForwardAsync(0, 1);
        await rig.RecomputeAsync(0, 12);
        Assert.True(await rig.CountAsync("\"generation\" = 1") > 0);
    }

    [Fact]
    public async Task Gate_ADeferredSweepStaysRecorded_WhileTheTenantIsMarked_AndRunsAfterTheRelease()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration.CrateRecomputeOptions
        {
            SweepGracePeriod = TimeSpan.FromMilliseconds(1)
        });
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var raw = OctoObjectId.GenerateNewId();
        var rollup = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, raw, 24);
        var type = new RtCkId<CkTypeId>("Test", new CkTypeId("Sensor"));
        var specs = new[] { new CkRollupAggregationSpec("Value", CkRollupFunction.Sum, null) };
        var snapshot = new RollupArchiveSnapshot(rollup, type, CkArchiveStatus.Activated, "hourly", [new RollupSourceReference(raw)],
            TimeSpan.FromHours(1), TimeSpan.Zero, Origin.AddHours(24), specs, null);
        var store = new InMemoryRollupStore(snapshot);
        var repo = fixture.NewReconcileRepository(tenant, [snapshot], [raw], new RecomputeProcessState(), options, store);
        await repo.EnsureArchiveCreatedAsync(new ArchiveSnapshot(rollup, type, CkArchiveStatus.Activated, "hourly",
            RollupColumnGenerator.Generate(specs)) { RollupAggregations = specs, Period = TimeSpan.FromHours(1) });
        var source = new ArchiveSnapshot(raw, type, CkArchiveStatus.Activated, "raw", [new CkArchiveColumnSpec("Value", true, false)]);
        var live = TenantSchema.QualifiedArchiveTable(tenant, rollup.ToString());
        await repo.ExecuteAsync(source, snapshot, Origin, Origin.AddHours(12), null, Ct); // generation 1, sweep deferred
        await repo.ExecuteAsync(source, snapshot, Origin, Origin.AddHours(12), null, Ct); // generation 2, its predecessor stays
        await fixture.SqlAsync($"REFRESH TABLE {live}");
        Assert.Equal(12, await fixture.CountAsync($"SELECT COUNT(*) FROM {live} WHERE \"generation\" = 1"));

        await repo.SetRestoreReconcilePendingAsync(true, Ct);
        await Task.Delay(50, Ct); // the grace period of one millisecond is over
        Assert.Equal(0, await repo.SweepSupersededGenerationsAsync(Ct));
        await fixture.SqlAsync($"REFRESH TABLE {live}");
        Assert.Equal(12, await fixture.CountAsync($"SELECT COUNT(*) FROM {live} WHERE \"generation\" = 1")); // not swept

        await repo.SetRestoreReconcilePendingAsync(false, Ct);
        Assert.True(await repo.SweepSupersededGenerationsAsync(Ct) > 0);
        await fixture.SqlAsync($"REFRESH TABLE {live}");
        Assert.Equal(0, await fixture.CountAsync($"SELECT COUNT(*) FROM {live} WHERE \"generation\" = 1"));
        Assert.Equal(12, await fixture.CountAsync($"SELECT COUNT(*) FROM {live} WHERE \"generation\" = 2"));
    }

    [Fact]
    public async Task Repair_RunsWhileTheTenantIsMarked_ItsOwnRecomputeIsNotHeldBack_AndItClearsTheMarker()
    {
        var rig = await RepairRig.CreateAsync(fixture);
        await rig.RecomputeAsync(0, 24); // generation 1
        var reference = await rig.ActiveViewAsync();
        await rig.SqlAsync($"DELETE FROM {rig.Live} WHERE \"generation\" = 1");
        await rig.Repository.SetRestoreReconcilePendingAsync(true, Ct);
        await Assert.ThrowsAsync<RestoreReconcilePendingException>(() => rig.RecomputeAsync(0, 24)); // the normal path is blocked

        var report = await rig.Repository.RepairRollupsAsync(null, false, Ct);

        Assert.True(report.IsOk);
        Assert.Equal(reference, await rig.ActiveViewAsync());
        Assert.False(await rig.Repository.IsRestoreReconcilePendingAsync(Ct));
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
