using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     AB#6968 against a real CrateDB: the sweep grace period of a rollup recompute. A recompute flips a range to a new
///     generation and keeps the superseded rows and pointer entries until the flip time plus the grace period has passed;
///     the pending sweep is a CrateDB row, so a restart does not lose it; and a native dump whose generation map was
///     copied before a full recompute (flip and sweep) still finds the rows that map names, when the data copy follows
///     within the grace period.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class SweepGraceIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly TimeSpan Grace = TimeSpan.FromHours(2);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Runs <c>action</c> once, right after the first statement that matches has been executed.</summary>
    private sealed class HookAfterClient(ICrateCopyClient inner, Func<string, bool> trigger, Func<Task> action) : ICrateCopyClient
    {
        private bool _done;

        public async Task<CrateStatementResult> ExecuteStatementAsync(
            string tenantId, CopyStatement statement, TimeSpan commandTimeout, CancellationToken cancellationToken)
        {
            var result = await inner.ExecuteStatementAsync(tenantId, statement, commandTimeout, cancellationToken);
            if (!_done && trigger(statement.Redacted))
            {
                _done = true;
                await action();
            }

            return result;
        }
    }

    /// <summary>A raw archive with 48 hourly rows, a rollup of it with an hourly sum, and an executor on a manual clock.</summary>
    private sealed class Rig
    {
        private readonly CrateStagingCopyFixture _fixture;
        private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();
        private readonly ArchiveSnapshot _raw;
        private readonly RollupArchiveSnapshot _rollupSnapshot;
        private readonly TimeSpan _grace;

        public Rig(CrateStagingCopyFixture fixture, TimeSpan grace, DateTimeOffset now)
        {
            _fixture = fixture;
            _grace = grace;
            Clock = new ManualClock(now);
            Tenant = CrateStagingCopyFixture.NewTenantId();
            RawRtId = OctoObjectId.GenerateNewId();
            RollupRtId = OctoObjectId.GenerateNewId();
            var type = new RtCkId<CkTypeId>("Test", new CkTypeId("Sensor"));
            var specs = new[] { new CkRollupAggregationSpec("Value", CkRollupFunction.Sum, null) };
            _raw = new ArchiveSnapshot(RawRtId, type, CkArchiveStatus.Activated, "raw",
                new[] { new CkArchiveColumnSpec("Value", true, false) });
            RollupArchive = new ArchiveSnapshot(RollupRtId, type, CkArchiveStatus.Activated, "hourly", RollupColumnGenerator.Generate(specs))
            {
                RollupAggregations = specs,
                Period = TimeSpan.FromHours(1)
            };
            _rollupSnapshot = new RollupArchiveSnapshot(RollupRtId, type, CkArchiveStatus.Activated, "hourly",
                new[] { new RollupSourceReference(RawRtId) }, TimeSpan.FromHours(1), TimeSpan.Zero, null, specs, null);
            A.CallTo(() => _store.GetAsync(RawRtId)).Returns(_raw);
            A.CallTo(() => _store.GetAsync(RollupRtId)).Returns(RollupArchive);
            State = new RecomputeProcessState();
        }

        public string Tenant { get; }
        public OctoObjectId RawRtId { get; }
        public OctoObjectId RollupRtId { get; }
        public ArchiveSnapshot RollupArchive { get; }
        public ManualClock Clock { get; }
        public RecomputeProcessState State { get; private set; }
        public string LiveTable => TenantSchema.QualifiedArchiveTable(Tenant, RollupRtId.ToString());
        public string GenMap => GenerationMapSqlBuilder.GenMapTable(Tenant, RollupRtId.ToString());
        public string SweepTable => PendingSweepSqlBuilder.SweepTable(Tenant, RollupRtId.ToString());

        public CrateDbArchiveRecomputeExecutor Executor { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            await _fixture.CreateRawArchiveAsync(Tenant, RawRtId, hourlyRows: 48);
            await _fixture.NewRollupRepository(Tenant, RollupRtId).EnsureArchiveCreatedAsync(RollupArchive);
            Restart();
        }

        /// <summary>A restarted process: a new executor and an empty process state - only the CrateDB tables are left.</summary>
        public void Restart()
        {
            State = new RecomputeProcessState();
            Executor = new CrateDbArchiveRecomputeExecutor(Tenant, _fixture.Client, _fixture.Client, _store,
                NullLogger.Instance, rollupArchiveStore: null, sliceTargetRows: 1000, State, _grace, Clock);
        }

        /// <summary>Recomputes the first day (24 hourly buckets).</summary>
        public Task<RecomputeExecutionResult> RecomputeAsync() => Executor.ExecuteAsync(
            _raw, _rollupSnapshot, CrateStagingCopyFixture.RollupOrigin, CrateStagingCopyFixture.RollupOrigin.AddHours(24), null, Ct);

        public async Task<long> RowsAsync(long generation)
        {
            await _fixture.SqlAsync($"REFRESH TABLE {LiveTable}");
            return await _fixture.CountAsync($"SELECT COUNT(*) FROM {LiveTable} WHERE \"generation\" = {generation}");
        }

        public async Task<List<string>> PointersAsync()
        {
            await _fixture.SqlAsync($"REFRESH TABLE {GenMap}");
            return (await _fixture.QueryAsync($"SELECT \"range_start\", \"range_end\", \"rtid_scope\", \"generation\" FROM {GenMap} ORDER BY 1, 2, 3"))
                .Select(r => string.Join("|", r.Values)).ToList();
        }

        public async Task<long> PendingAsync()
        {
            await _fixture.SqlAsync($"REFRESH TABLE {SweepTable}");
            return await _fixture.CountAsync($"SELECT COUNT(*) FROM {SweepTable}");
        }

        /// <summary>What a reader sees: the sum over the rows of the generation the newest pointer entry names.</summary>
        public async Task<double> ActiveSumAsync()
        {
            await _fixture.SqlAsync($"REFRESH TABLE {LiveTable}");
            var generation = await _fixture.CountAsync($"SELECT COALESCE(MAX(\"generation\"), 0) FROM {GenMap}");
            return Convert.ToDouble((await _fixture.QueryAsync(
                $"SELECT SUM(\"value_sum\") AS s FROM {LiveTable} WHERE \"generation\" = {generation}")).Single()["s"]);
        }
    }

    private static DateTimeOffset T0 => new(2026, 5, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AfterTheFlip_PreviousRowsAndPointerEntriesStayUntilTheGracePeriodPassed_AndSurviveARestart()
    {
        var rig = new Rig(fixture, Grace, T0);
        await rig.InitializeAsync();

        await rig.RecomputeAsync(); // generation 1
        await rig.RecomputeAsync(); // generation 2, same range: the pointer entry is replaced

        // both versions are in the table, the pointer names the newer one, both sweeps are recorded
        Assert.Equal(24, await rig.RowsAsync(1));
        Assert.Equal(24, await rig.RowsAsync(2));
        Assert.Single(await rig.PointersAsync());
        Assert.EndsWith("|2", (await rig.PointersAsync()).Single(), StringComparison.Ordinal);
        Assert.Equal(2, await rig.PendingAsync());
        var valueDuringGrace = await rig.ActiveSumAsync();
        Assert.True(valueDuringGrace > 0);

        // one millisecond short of the grace period: nothing is swept, also not by a restarted process
        rig.Clock.Now = T0 + Grace - TimeSpan.FromMilliseconds(1);
        rig.Restart();
        Assert.Equal(0, await rig.Executor.SweepDueAsync(rig.RollupRtId, Ct));
        Assert.Equal(24, await rig.RowsAsync(1));

        // the grace period has passed: the restarted process finds the records in CrateDB and sweeps
        rig.Clock.Now = T0 + Grace;
        Assert.Equal(2, await rig.Executor.SweepDueAsync(rig.RollupRtId, Ct));

        Assert.Equal(0, await rig.RowsAsync(1));
        Assert.Equal(24, await rig.RowsAsync(2));
        Assert.Equal(0, await rig.PendingAsync());
        Assert.Equal(valueDuringGrace, await rig.ActiveSumAsync()); // the readers' value is the same before and after
    }

    [Fact]
    public async Task GraceZero_SweepsAtOnce_AsBefore()
    {
        var rig = new Rig(fixture, TimeSpan.Zero, T0);
        await rig.InitializeAsync();

        await rig.RecomputeAsync();
        await rig.RecomputeAsync();

        Assert.Equal(0, await rig.RowsAsync(1));
        Assert.Equal(24, await rig.RowsAsync(2));
        Assert.Equal(0, await fixture.CountAsync(
            $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '{TenantSchema.SchemaName(rig.Tenant)}' AND table_name LIKE '%__sweep'"));
    }

    /// <summary>
    ///     The generation map is copied while the pointer names generation 1; a full recompute (flip and sweep) runs
    ///     before the data is copied. Restored, the pointer names generation 1 - are its rows there?
    /// </summary>
    private async Task<long> RestoredRowsOfThePointedGenerationAsync(TimeSpan grace)
    {
        var rig = new Rig(fixture, grace, T0);
        await rig.InitializeAsync();
        await rig.RecomputeAsync(); // generation 1

        var hooked = new HookAfterClient(fixture.Client,
            sql => sql.StartsWith("COPY", StringComparison.Ordinal) && sql.Contains("__genmap", StringComparison.Ordinal),
            async () =>
            {
                await rig.RecomputeAsync();      // generation 2: flip and, without a grace period, sweep of generation 1
                rig.Clock.Now += TimeSpan.FromMinutes(30);
                await rig.Executor.SweepDueAsync(rig.RollupRtId, Ct); // a sweep tick well within the grace period
            });
        var runner = new StagingCopyRunner(NullLogger.Instance, rig.Tenant, hooked);
        var table = TenantSchema.ArchiveTableName(rig.RollupRtId.ToString());
        var target = new ArchiveCopyTarget(rig.RollupRtId, TenantSchema.SchemaName(rig.Tenant), table, "window_start", table + "__genmap");
        var options = new ArchiveCopyOptions();
        var location = CrateStagingCopyFixture.NewFileLocation();
        var plan = await runner.PlanAsync(target, options, Ct);
        await fixture.PrepareDirectoriesAsync(plan, location);

        var results = new List<ArchiveCopySliceResult>();
        await foreach (var result in runner.CopyToAsync(target, plan, location, options, Ct))
        {
            results.Add(result);
        }

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));
        var copiedPlan = RollupArchiveDumper.CopiedPlan(plan, results);

        var restored = new Rig(fixture, Grace, T0);
        await restored.InitializeAsync();
        var repo = fixture.NewRollupRepository(restored.Tenant, restored.RollupRtId);
        var load = await repo.LoadArchiveFromStagingAsync(restored.RollupRtId, copiedPlan, location, options, Ct);
        Assert.True(load.RowCountVerified);

        var pointers = await restored.PointersAsync();
        var pointed = long.Parse(pointers.Single().Split('|')[^1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(1, pointed); // the map was copied before the flip to generation 2
        return await restored.RowsAsync(pointed);
    }

    [Fact]
    public async Task DumpWithTheMapCopiedBeforeAFullRecompute_WithTheGracePeriod_RestoresAPointerWhoseRowsExist()
        => Assert.Equal(24, await RestoredRowsOfThePointedGenerationAsync(Grace));

    [Fact]
    public async Task DumpWithTheMapCopiedBeforeAFullRecompute_WithoutAGracePeriod_RestoresAPointerWithoutRows()
        => Assert.Equal(0, await RestoredRowsOfThePointedGenerationAsync(TimeSpan.Zero)); // what the grace period prevents
}
