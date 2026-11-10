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
///     AB#8680 without memory pressure: the forward rollup aggregation, the recompute's staging aggregation and its
///     staging-to-live copy notice rows CrateDB did not store, write them once more, and fail loudly when they are still
///     missing — against a real CrateDB, in seconds.
/// </summary>
/// <remarks>
///     The loss is injected around the real client: the chosen <c>INSERT … SELECT</c> is executed, then the rows of two
///     entities are deleted from its target and the affected count is reported without them — what CrateDB answers when a
///     tripped memory breaker ended a shard request. "Once" drops only on the first execution (the retry must restore
///     the rows), "always" on every execution (the write must fail and a recompute must leave the pointer on the previous
///     generation). The same checks under real memory pressure: the <c>Category=Stress</c> class
///     <see cref="RollupUnderMemoryPressureIntegrationTests" />.
/// </remarks>
[Collection(CrateStagingCopyCollection.Name)]
public class RollupShortfallIntegrationTests(CrateStagingCopyFixture fixture)
{
    private const int Entities = 200;

    private static readonly DateTime Origin = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Sensor"));
    private static readonly CkRollupAggregationSpec[] Specs = [new("Value", CkRollupFunction.Sum, null)];

    /// <summary>The entities whose rows the injected loss removes.</summary>
    private const string DroppedEntities = "('rt0', 'rt1')";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private enum Target
    {
        Live,
        Staging
    }

    private enum Drop
    {
        Once,
        Always
    }

    [Fact]
    public async Task ForwardAggregation_ShortOnce_IsWrittenOnceMore_AndTheBucketIsComplete()
    {
        await using var rig = await Rig.CreateAsync(fixture);
        var loss = rig.InjectLoss(Target.Live, "TRUE", Drop.Once);

        await rig.Repository.AggregateBucketAsync(rig.Source, rig.RollupSnapshot, Origin, Origin.AddHours(1), Ct);

        Assert.Equal(2, loss.Executions);
        await rig.AssertGenerationCompleteAsync(generation: 0);
    }

    [Fact]
    public async Task ForwardAggregation_StillShortAfterTheRepeat_FailsNamingTheTable()
    {
        await using var rig = await Rig.CreateAsync(fixture);
        var loss = rig.InjectLoss(Target.Live, "TRUE", Drop.Always);

        var ex = await Assert.ThrowsAsync<StreamDataWriteIncompleteException>(() =>
            rig.Repository.AggregateBucketAsync(rig.Source, rig.RollupSnapshot, Origin, Origin.AddHours(1), Ct));

        Assert.Equal(2, loss.Executions);
        Assert.Equal(rig.LiveTable, ex.QualifiedTable);
        Assert.Equal(Entities, ex.RowsSent);
        Assert.Equal(2, ex.RowsMissing);
    }

    [Fact]
    public async Task Recompute_StagingAggregationShortOnce_IsWrittenOnceMore_AndTheCompleteGenerationIsCommitted()
    {
        await using var rig = await Rig.CreateAsync(fixture);
        await rig.RecomputeAsync();
        var loss = rig.InjectLoss(Target.Staging, "TRUE", Drop.Once);

        await rig.RecomputeAsync();

        Assert.Equal(2, loss.Executions);
        Assert.Equal(2, await rig.PointerGenerationAsync());
        await rig.AssertGenerationCompleteAsync(generation: 2);
    }

    [Fact]
    public async Task Recompute_CopyShortOnce_IsCopiedOnceMore_AndTheCompleteGenerationIsCommitted()
    {
        await using var rig = await Rig.CreateAsync(fixture);
        await rig.RecomputeAsync();
        var loss = rig.InjectLoss(Target.Live, "\"generation\" = 2", Drop.Once);

        await rig.RecomputeAsync();

        Assert.Equal(2, loss.Executions);
        Assert.Equal(2, await rig.PointerGenerationAsync());
        await rig.AssertGenerationCompleteAsync(generation: 2);
    }

    [Fact]
    public async Task Recompute_CopyStillShort_FailsBeforeTheFlip_AndThePreviousGenerationStaysLive()
    {
        await using var rig = await Rig.CreateAsync(fixture);
        await rig.RecomputeAsync();
        var loss = rig.InjectLoss(Target.Live, "\"generation\" = 2", Drop.Always);

        var ex = await Assert.ThrowsAsync<StreamDataWriteIncompleteException>(rig.RecomputeAsync);

        Assert.Equal(2, loss.Executions);
        Assert.Equal(rig.LiveTable, ex.QualifiedTable);
        Assert.Equal(1, await rig.PointerGenerationAsync());
        await rig.AssertGenerationCompleteAsync(generation: 1);
    }

    /// <summary>Executions of the statement the loss is injected into, retries included.</summary>
    private sealed class Loss
    {
        public int Executions { get; set; }
    }

    /// <summary>One raw source (one hour, four rows per entity) and one hourly sum rollup of it, in a tenant of their own.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly CrateStagingCopyFixture _fixture;
        private readonly string _tenant;
        private readonly string _sourceTable;
        private readonly OctoObjectId _rollupRtId = OctoObjectId.GenerateNewId();
        private readonly ArchiveSnapshot _rollupArchive;
        private readonly IStreamDataDatabaseClient _client;

        private Rig(CrateStagingCopyFixture fixture)
        {
            _fixture = fixture;
            _tenant = CrateStagingCopyFixture.NewTenantId();
            var sourceRtId = OctoObjectId.GenerateNewId();
            _sourceTable = TenantSchema.QualifiedArchiveTable(_tenant, sourceRtId.ToString());
            Source = new ArchiveSnapshot(sourceRtId, Type, CkArchiveStatus.Activated, "raw",
                new[] { new CkArchiveColumnSpec("Value", true, false) });
            _rollupArchive = new ArchiveSnapshot(_rollupRtId, Type, CkArchiveStatus.Activated, "hourly",
                RollupColumnGenerator.Generate(Specs))
            {
                RollupAggregations = Specs,
                Period = TimeSpan.FromHours(1)
            };
            LiveTable = TenantSchema.QualifiedArchiveTable(_tenant, _rollupRtId.ToString());
            StagingTable = RollupRecomputeSqlBuilder.StagingTable(_tenant, _rollupRtId.ToString());

            var store = A.Fake<IArchiveRuntimeStore>();
            A.CallTo(() => store.GetAsync(sourceRtId)).Returns(Source);
            A.CallTo(() => store.GetAsync(_rollupRtId)).Returns(_rollupArchive);

            // Every call goes to the real client unless a test injects a loss.
            _client = A.Fake<IStreamDataDatabaseClient>(o => o.Wrapping(fixture.Client));
            Repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
                A.Fake<ICkCacheService>(), _client, fixture.Client, fixture.Configuration, _tenant, store,
                A.Fake<IFormulaEngine>());
            Executor = new CrateDbArchiveRecomputeExecutor(_tenant, _client, fixture.Client, store, NullLogger.Instance,
                rollupArchiveStore: null, sliceTargetRows: 100_000, new RecomputeProcessState(), TimeSpan.Zero,
                TimeProvider.System);
        }

        public ArchiveSnapshot Source { get; }

        public string LiveTable { get; }

        private string StagingTable { get; }

        public CrateDbStreamDataRepository Repository { get; }

        private CrateDbArchiveRecomputeExecutor Executor { get; }

        public RollupArchiveSnapshot RollupSnapshot => new(_rollupRtId, Type, CkArchiveStatus.Activated, "hourly",
            new[] { new RollupSourceReference(Source.RtId) }, TimeSpan.FromHours(1), TimeSpan.Zero, null, Specs, null);

        public static async Task<Rig> CreateAsync(CrateStagingCopyFixture fixture)
        {
            var rig = new Rig(fixture);
            await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(rig._sourceTable,
                [new ArchiveColumnDdl("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)],
                numberOfShards: 2, numberOfReplicas: 0));
            var originMs = new DateTimeOffset(Origin).ToUnixTimeMilliseconds();
            await fixture.SqlAsync(
                $"INSERT INTO {rig._sourceTable} (\"rtid\", \"timestamp\", \"cktypeid\", \"value\") " +
                $"SELECT 'rt' || CAST(CAST(generate_series AS BIGINT) % {Entities} AS TEXT), " +
                $"CAST({originMs} + (CAST(generate_series AS BIGINT) / {Entities}) * 900000 AS TIMESTAMP WITH TIME ZONE), " +
                $"'Test/Sensor-1', (CAST(generate_series AS BIGINT) % 97) * 1.5 " +
                $"FROM generate_series(0, {Entities * 4 - 1})");
            await fixture.SqlAsync($"REFRESH TABLE {rig._sourceTable}");
            await rig.Repository.EnsureArchiveCreatedAsync(rig._rollupArchive);
            return rig;
        }

        /// <summary>
        ///     From now on, an <c>INSERT</c> into the target loses the rows of <see cref="DroppedEntities" /> that match
        ///     <paramref name="predicate" />: it is executed, the rows are deleted, and the count is reported without them.
        /// </summary>
        public Loss InjectLoss(Target target, string predicate, Drop drop)
        {
            var table = target == Target.Live ? LiveTable : StagingTable;
            var loss = new Loss();
            A.CallTo(() => _client.ExecuteNonQueryAsync(_tenant,
                    A<string>.That.StartsWith($"INSERT INTO {table} "), A<CrateStatementClass>._, A<CancellationToken>._))
                .ReturnsLazily(async (string tenant, string sql, CrateStatementClass statementClass, CancellationToken ct) =>
                {
                    var affected = await _fixture.Client.ExecuteNonQueryAsync(tenant, sql, statementClass, ct);
                    loss.Executions++;
                    if (drop == Drop.Once && loss.Executions > 1)
                    {
                        return affected;
                    }

                    await _fixture.Client.RefreshArchiveTableAsync(tenant, table, statementClass, ct);
                    var deleted = await _fixture.Client.ExecuteNonQueryAsync(tenant,
                        $"DELETE FROM {table} WHERE \"rtid\" IN {DroppedEntities} AND {predicate}", statementClass, ct);
                    await _fixture.Client.RefreshArchiveTableAsync(tenant, table, statementClass, ct);
                    return affected - deleted;
                });
            return loss;
        }

        public Task<RecomputeExecutionResult> RecomputeAsync() => Executor.ExecuteAsync(
            Source, RollupSnapshot, Origin, Origin.AddHours(1), null, Ct);

        public async Task<long> PointerGenerationAsync()
        {
            var genMap = GenerationMapSqlBuilder.GenMapTable(_tenant, _rollupRtId.ToString());
            await _fixture.SqlAsync($"REFRESH TABLE {genMap}");
            return await _fixture.CountAsync($"SELECT COALESCE(MAX(\"generation\"), 0) FROM {genMap}");
        }

        /// <summary>The generation holds one row per entity, and its sums are the source's.</summary>
        public async Task AssertGenerationCompleteAsync(long generation)
        {
            await _fixture.SqlAsync($"REFRESH TABLE {LiveTable}");
            var rollup = (await _fixture.QueryAsync(
                $"SELECT COUNT(*) AS n, COALESCE(SUM(\"value_sum\"), 0) AS s FROM {LiveTable} WHERE \"generation\" = {generation}")).Single();
            var source = (await _fixture.QueryAsync($"SELECT COALESCE(SUM(\"value\"), 0) AS s FROM {_sourceTable}")).Single();
            Assert.Equal(Entities, Convert.ToInt64(rollup["n"], CultureInfo.InvariantCulture));
            Assert.Equal(Convert.ToDouble(source["s"], CultureInfo.InvariantCulture),
                Convert.ToDouble(rollup["s"], CultureInfo.InvariantCulture), 6);
        }

        public async ValueTask DisposeAsync()
        {
            var tables = await _fixture.QueryAsync(
                $"SELECT table_name FROM information_schema.tables WHERE table_schema = '{TenantSchema.SchemaName(_tenant)}'");
            foreach (var row in tables)
            {
                await _fixture.SqlAsync($"DROP TABLE IF EXISTS \"{TenantSchema.SchemaName(_tenant)}\".\"{row["table_name"]}\"");
            }
        }
    }
}
