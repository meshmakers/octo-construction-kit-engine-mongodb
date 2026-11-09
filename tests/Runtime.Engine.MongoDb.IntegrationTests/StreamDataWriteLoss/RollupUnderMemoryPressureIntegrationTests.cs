using System.Globalization;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataWriteLoss;

/// <summary>
///     AB#8680 against a real CrateDB whose query circuit breaker trips under parallel load (the fixture of AB#8679). The
///     forward rollup aggregation and the recompute (staging aggregation and staging-to-live copy) are
///     <c>INSERT … SELECT</c> statements; CrateDB can end a shard request of them and answer without an error. Before the
///     fix a bucket was then stored in part and the recompute flipped its generation pointer to an incomplete
///     generation. Now each of them either stores every row (the rollup sums match the source) or fails loudly — and a
///     failed recompute leaves the pointer on the previous, complete generation.
/// </summary>
/// <remarks>
///     <see cref="Rollups" /> rollups of one raw source (<see cref="Entities" /> entities, four rows an hour each) run in
///     parallel, <see cref="Rounds" /> rounds per test. A test that saw no breaker trip proves nothing and is skipped with
///     that reason instead of passing.
/// </remarks>
[Collection(CrateMemoryPressureCollection.Name)]
public class RollupUnderMemoryPressureIntegrationTests(CrateMemoryPressureFixture fixture)
{
    private const int Rollups = 6;
    private const int Entities = 20_000;
    private const int Buckets = 3;
    private const int Rounds = 2;

    /// <summary>
    ///     Query breaker limit (share of the 512 MB heap) under which the parallel <c>INSERT … SELECT</c> statements of this
    ///     test trip the breaker on crate 6.4.6 (measured 2026-11-09, AB#8680).
    /// </summary>
    private static readonly string BreakerLimit =
        Environment.GetEnvironmentVariable("OCTO_TEST_AB8680_BREAKER_LIMIT") is { Length: > 0 } limit ? limit : "15%";

    private static readonly DateTime Origin = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Sensor"));
    private static readonly CkRollupAggregationSpec[] Specs = [new("Value", CkRollupFunction.Sum, null)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ForwardAggregation_WritesEveryRow_OrFailsLoudly()
    {
        var tripsBefore = await fixture.BreakerTripsAsync();
        var failures = new List<string>();
        var loud = new List<Exception>();

        for (var round = 0; round < Rounds; round++)
        {
            var rig = await Rig.CreateAsync(fixture);
            var outcomes = new Exception?[Rollups, Buckets];
            await fixture.SetQueryBreakerLimitAsync(BreakerLimit);
            try
            {
                await Task.WhenAll(Enumerable.Range(0, Rollups).Select(r => Task.Run(async () =>
                {
                    for (var b = 0; b < Buckets; b++)
                    {
                        try
                        {
                            await rig.Repository(r).AggregateBucketAsync(
                                rig.Source, rig.RollupSnapshot(r), Origin.AddHours(b), Origin.AddHours(b + 1), Ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            outcomes[r, b] = ex;
                        }
                    }
                })));
            }
            finally
            {
                await fixture.ResetQueryBreakerLimitAsync();
            }

            for (var r = 0; r < Rollups; r++)
            {
                for (var b = 0; b < Buckets; b++)
                {
                    if (outcomes[r, b] is { } error)
                    {
                        loud.Add(error);
                        continue;
                    }

                    var (rows, sum) = await rig.RollupBucketAsync(r, generation: 0, b);
                    var (expectedRows, expectedSum) = await rig.SourceBucketAsync(b);
                    if (rows != expectedRows || Math.Abs(sum - expectedSum) > 1e-6)
                    {
                        failures.Add($"round {round} rollup {r} bucket {b}: aggregation succeeded, {rows} of {expectedRows} rows, sum {sum} of {expectedSum}");
                    }
                }
            }
        }

        await SkipUnlessTheBreakerTripped(tripsBefore, loud, "forward");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task Recompute_FlipsOnlyToACompleteGeneration_OrFailsLoudlyAndKeepsThePreviousOne()
    {
        var tripsBefore = await fixture.BreakerTripsAsync();
        var failures = new List<string>();
        var loud = new List<Exception>();

        for (var round = 0; round < Rounds; round++)
        {
            var rig = await Rig.CreateAsync(fixture);

            // Generation 1 of every rollup without memory pressure: the complete, live generation.
            // CrateDB can still be short of memory from the previous test: a set-up run may fail loudly (seen once at the
            // default breaker limit, 10,000 of 20,000 staged rows missing) and is then simply run again.
            for (var r = 0; r < Rollups; r++)
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await rig.RecomputeAsync(r);
                        break;
                    }
                    catch (StreamDataWriteIncompleteException) when (attempt < 3)
                    {
                    }
                }

                Assert.Equal(1, await rig.PointerGenerationAsync(r));
            }

            var outcomes = new Exception?[Rollups];
            await fixture.SetQueryBreakerLimitAsync(BreakerLimit);
            try
            {
                await Task.WhenAll(Enumerable.Range(0, Rollups).Select(r => Task.Run(async () =>
                {
                    try
                    {
                        await rig.RecomputeAsync(r);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        outcomes[r] = ex;
                    }
                })));
            }
            finally
            {
                await fixture.ResetQueryBreakerLimitAsync();
            }

            var (expectedRows, expectedSum) = await rig.SourceTotalAsync();
            for (var r = 0; r < Rollups; r++)
            {
                var pointer = await rig.PointerGenerationAsync(r);
                if (outcomes[r] is { } error)
                {
                    loud.Add(error);
                    // The run failed: readers must still be on the complete generation 1.
                    if (pointer != 1)
                    {
                        failures.Add($"round {round} rollup {r}: recompute failed ({error.GetType().Name}) but the pointer names generation {pointer}");
                        continue;
                    }
                }
                else if (pointer != 2)
                {
                    failures.Add($"round {round} rollup {r}: recompute succeeded but the pointer names generation {pointer}");
                    continue;
                }

                var (rows, sum) = await rig.RollupGenerationAsync(r, pointer);
                if (rows != expectedRows || Math.Abs(sum - expectedSum) > 1e-6)
                {
                    failures.Add($"round {round} rollup {r}: the pointer names generation {pointer} with {rows} of {expectedRows} rows, sum {sum} of {expectedSum}" +
                                 (outcomes[r] is null ? " (recompute succeeded)" : " (recompute failed)"));
                }
            }
        }

        await SkipUnlessTheBreakerTripped(tripsBefore, loud, "recompute");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private async Task SkipUnlessTheBreakerTripped(int tripsBefore, List<Exception> loud, string what)
    {
        var trips = await fixture.BreakerTripsAsync() - tripsBefore;
        var byType = string.Join(", ", loud.GroupBy(e => e.GetType().Name).Select(g => $"{g.Key} x{g.Count()}"));
        TestContext.Current.SendDiagnosticMessage(
            $"AB#8680 {what} stress: {trips} breaker trips, {loud.Count} runs failed loudly ({byType}); first: {loud.FirstOrDefault()?.Message}");
        if (trips == 0)
        {
            Assert.Skip("The CrateDB memory breaker never tripped; the test proves nothing on this machine.");
        }
    }

    /// <summary>One raw source and <see cref="Rollups" /> hourly sum rollups of it, in a tenant of their own.</summary>
    private sealed class Rig
    {
        private readonly CrateMemoryPressureFixture _fixture;
        private readonly string _tenant;
        private readonly string _sourceTable;
        private readonly OctoObjectId[] _rollupRtIds;
        private readonly ArchiveSnapshot[] _rollupArchives;
        private readonly CrateDbStreamDataRepository[] _repositories;
        private readonly CrateDbArchiveRecomputeExecutor[] _executors;

        private Rig(CrateMemoryPressureFixture fixture)
        {
            _fixture = fixture;
            _tenant = CrateStagingCopyFixture.NewTenantId();
            var sourceRtId = OctoObjectId.GenerateNewId();
            _sourceTable = TenantSchema.QualifiedArchiveTable(_tenant, sourceRtId.ToString());
            Source = new ArchiveSnapshot(sourceRtId, Type, CkArchiveStatus.Activated, "raw",
                new[] { new CkArchiveColumnSpec("Value", true, false) });
            _rollupRtIds = Enumerable.Range(0, Rollups).Select(_ => OctoObjectId.GenerateNewId()).ToArray();
            _rollupArchives = _rollupRtIds.Select(rt => new ArchiveSnapshot(rt, Type, CkArchiveStatus.Activated, "hourly",
                RollupColumnGenerator.Generate(Specs))
            {
                RollupAggregations = Specs,
                Period = TimeSpan.FromHours(1)
            }).ToArray();

            var store = A.Fake<IArchiveRuntimeStore>();
            A.CallTo(() => store.GetAsync(sourceRtId)).Returns(Source);
            foreach (var archive in _rollupArchives)
            {
                A.CallTo(() => store.GetAsync(archive.RtId)).Returns(archive);
            }

            _repositories = _rollupRtIds.Select(_ => new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
                A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, _tenant, store,
                A.Fake<IFormulaEngine>())).ToArray();
            // One slice for the whole copy: the largest INSERT … SELECT the recompute can send.
            _executors = _rollupRtIds.Select(_ => new CrateDbArchiveRecomputeExecutor(_tenant, fixture.Client, fixture.Client, store,
                NullLogger.Instance, rollupArchiveStore: null, sliceTargetRows: 1_000_000, new RecomputeProcessState(),
                TimeSpan.Zero, TimeProvider.System)).ToArray();
        }

        public ArchiveSnapshot Source { get; }

        public static async Task<Rig> CreateAsync(CrateMemoryPressureFixture fixture)
        {
            var rig = new Rig(fixture);
            await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(rig._sourceTable,
                [new ArchiveColumnDdl("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)],
                numberOfShards: 4, numberOfReplicas: 0));
            // Four rows an hour per entity; the value repeats every 97 rows so that a lost row changes the sum.
            var originMs = new DateTimeOffset(Origin).ToUnixTimeMilliseconds();
            await fixture.SqlAsync(
                $"INSERT INTO {rig._sourceTable} (\"rtid\", \"timestamp\", \"cktypeid\", \"value\") " +
                $"SELECT 'rt' || CAST(CAST(generate_series AS BIGINT) % {Entities} AS TEXT), " +
                $"CAST({originMs} + (CAST(generate_series AS BIGINT) / {Entities}) * 900000 AS TIMESTAMP WITH TIME ZONE), " +
                $"'Test/Sensor-1', (CAST(generate_series AS BIGINT) % 97) * 1.5 " +
                $"FROM generate_series(0, {Entities * 4 * Buckets - 1})");
            await fixture.SqlAsync($"REFRESH TABLE {rig._sourceTable}");
            for (var r = 0; r < Rollups; r++)
            {
                await rig._repositories[r].EnsureArchiveCreatedAsync(rig._rollupArchives[r]);
            }

            return rig;
        }

        public CrateDbStreamDataRepository Repository(int rollup) => _repositories[rollup];

        public RollupArchiveSnapshot RollupSnapshot(int rollup) => new(_rollupRtIds[rollup], Type, CkArchiveStatus.Activated, "hourly",
            new[] { new RollupSourceReference(Source.RtId) }, TimeSpan.FromHours(1), TimeSpan.Zero, null, Specs, null);

        public Task<RecomputeExecutionResult> RecomputeAsync(int rollup) => _executors[rollup].ExecuteAsync(
            Source, RollupSnapshot(rollup), Origin, Origin.AddHours(Buckets), null, Ct);

        private string LiveTable(int rollup) => TenantSchema.QualifiedArchiveTable(_tenant, _rollupRtIds[rollup].ToString());

        public async Task<long> PointerGenerationAsync(int rollup)
        {
            var genMap = GenerationMapSqlBuilder.GenMapTable(_tenant, _rollupRtIds[rollup].ToString());
            await _fixture.SqlAsync($"REFRESH TABLE {genMap}");
            return await _fixture.CountAsync($"SELECT COALESCE(MAX(\"generation\"), 0) FROM {genMap}");
        }

        public Task<(long Rows, double Sum)> RollupBucketAsync(int rollup, long generation, int bucket)
            => RowsAndSumAsync(LiveTable(rollup), "value_sum",
                $"\"generation\" = {generation} AND \"window_start\" = {Ms(Origin.AddHours(bucket))}");

        public Task<(long Rows, double Sum)> RollupGenerationAsync(int rollup, long generation)
            => RowsAndSumAsync(LiveTable(rollup), "value_sum", $"\"generation\" = {generation}");

        public async Task<(long Rows, double Sum)> SourceBucketAsync(int bucket)
        {
            var (_, sum) = await RowsAndSumAsync(_sourceTable, "value",
                $"\"timestamp\" >= {Ms(Origin.AddHours(bucket))} AND \"timestamp\" < {Ms(Origin.AddHours(bucket + 1))}");
            return (Entities, sum);
        }

        public async Task<(long Rows, double Sum)> SourceTotalAsync()
        {
            var (_, sum) = await RowsAndSumAsync(_sourceTable, "value", "TRUE");
            return ((long)Entities * Buckets, sum);
        }

        private async Task<(long Rows, double Sum)> RowsAndSumAsync(string table, string column, string where)
        {
            await _fixture.SqlAsync($"REFRESH TABLE {table}");
            var row = (await _fixture.QueryAsync(
                $"SELECT COUNT(*) AS n, COALESCE(SUM(\"{column}\"), 0) AS s FROM {table} WHERE {where}")).Single();
            return (Convert.ToInt64(row["n"], CultureInfo.InvariantCulture), Convert.ToDouble(row["s"], CultureInfo.InvariantCulture));
        }

        private static long Ms(DateTime value) => new DateTimeOffset(value).ToUnixTimeMilliseconds();
    }
}
