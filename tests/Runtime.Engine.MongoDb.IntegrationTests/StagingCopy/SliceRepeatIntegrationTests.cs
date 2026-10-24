using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     AB#7274 against a real CrateDB: a slice that is loaded again after a transient failure holds exactly its manifest
///     rows, whether the first attempt wrote all of them, some of them or none (raw archive, rollup data and generation map).
///     The failure is injected around a real client: the statement is executed, then the breaker error is reported.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class SliceRepeatIntegrationTests(CrateStagingCopyFixture fixture)
{
    private const string BreakerText = "CircuitBreakingException[[parent] Data too large, data for [upsert request] would be larger than limit]";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<ArchiveCopySliceResult>> Collect(IAsyncEnumerable<ArchiveCopySliceResult> results)
    {
        var list = new List<ArchiveCopySliceResult>();
        await foreach (var result in results)
        {
            list.Add(result);
        }

        return list;
    }

    private async Task<List<string>> RowsAsync(string table, string orderBy, string columns)
    {
        await fixture.SqlAsync($"REFRESH TABLE {table}");
        var rows = await fixture.QueryAsync($"SELECT {columns} FROM {table} ORDER BY {orderBy}");
        return rows.Select(r => string.Join("|", r.Values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))).ToList();
    }

    private static ArchiveCopyTarget RawTarget(string tenant, OctoObjectId id)
        => new(id, TenantSchema.SchemaName(tenant), TenantSchema.ArchiveTableName(id.ToString()), "timestamp");

    private static ArchiveCopyTarget RollupTarget(string tenant, OctoObjectId id)
        => new(id, TenantSchema.SchemaName(tenant), TenantSchema.ArchiveTableName(id.ToString()), "window_start",
            TenantSchema.ArchiveTableName(id.ToString()) + "__genmap");

    [Fact]
    public async Task RawSlice_FirstAttemptWroteEverythingThenFailed_TheRepeatLeavesExactlyTheSliceRows()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        var target = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, source, hourlyRows: 1500);
        await fixture.CreateRawArchiveAsync(tenant, target);
        var (plan, location) = await DumpAsync(tenant, RawTarget(tenant, source));
        var client = new FailAfterExecutingClient(fixture.Client, "FROM", failures: 1);

        var results = await Collect(new StagingCopyRunner(NullLogger.Instance, tenant, client, new InstantClock())
            .CopyFromAsync(RawTarget(tenant, target), plan, location, new ArchiveCopyOptions { TargetSliceRows = 400 }, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));
        Assert.Equal(1, client.Failed);
        Assert.Contains(client.Loads, l => l.Contains("overwrite_duplicates = true", StringComparison.Ordinal)
                                           && l.Contains("bulk_size = 5000", StringComparison.Ordinal));
        var columns = "\"rtid\", \"timestamp\", \"cktypeid\", \"value\"";
        Assert.Equal(await RowsAsync(TenantSchema.QualifiedArchiveTable(tenant, source.ToString()), "2", columns),
            await RowsAsync(TenantSchema.QualifiedArchiveTable(tenant, target.ToString()), "2", columns));
    }

    [Fact]
    public async Task RawSlice_PartlyLoadedBeforeTheFailure_TheRepeatLeavesExactlyTheSliceRows()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        var target = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, source, hourlyRows: 1500);
        await fixture.CreateRawArchiveAsync(tenant, target);
        var sourceTable = TenantSchema.QualifiedArchiveTable(tenant, source.ToString());
        var targetTable = TenantSchema.QualifiedArchiveTable(tenant, target.ToString());
        var (plan, location) = await DumpAsync(tenant, RawTarget(tenant, source));
        // what a refused attempt leaves behind: part of the first slice, already in the table (a plain re-run would fail on the keys)
        var first = plan.Slices[0];
        await fixture.SqlAsync($"INSERT INTO {targetTable} SELECT * FROM {sourceTable} " +
                               $"WHERE \"timestamp\" >= '{CopyStatement.FormatTs(first.FromUtc!.Value)}' AND \"timestamp\" < '{CopyStatement.FormatTs(first.FromUtc.Value.AddHours(100))}'");
        var client = new FailAfterExecutingClient(fixture.Client, "FROM", failures: 1, execute: false);

        var results = await Collect(new StagingCopyRunner(NullLogger.Instance, tenant, client, new InstantClock())
            .CopyFromAsync(RawTarget(tenant, target), plan, location, new ArchiveCopyOptions { TargetSliceRows = 400 }, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));
        Assert.Equal(1500, await fixture.CountAsync($"SELECT COUNT(*) FROM {targetTable}"));
        var columns = "\"rtid\", \"timestamp\", \"cktypeid\", \"value\"";
        Assert.Equal(await RowsAsync(sourceTable, "2", columns), await RowsAsync(targetTable, "2", columns));
    }

    [Fact]
    public async Task Rollup_DataSliceAndGenerationMap_AreRepeatedToExactlyTheirRows()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        var target = OctoObjectId.GenerateNewId();
        await fixture.CreateRollupAsync(tenant, source, generations: 4);
        await fixture.CreateRollupAsync(tenant, target);
        var (plan, location) = await DumpAsync(tenant, RollupTarget(tenant, source), targetSliceRows: 100);
        Assert.Contains(plan.Slices, s => s.Table == ArchiveCopyTable.GenerationMap);
        // the data slices and the map slice each fail once, after the statement ran
        var client = new FailAfterExecutingClient(fixture.Client, "FROM", failures: int.MaxValue, oncePerTable: true);

        var results = await Collect(new StagingCopyRunner(NullLogger.Instance, tenant, client, new InstantClock())
            .CopyFromAsync(RollupTarget(tenant, target), plan, location, new ArchiveCopyOptions { TargetSliceRows = 100 }, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));
        Assert.Equal(2, client.Failed); // one data slice, one map
        var sourceTable = TenantSchema.QualifiedArchiveTable(tenant, source.ToString());
        var targetTable = TenantSchema.QualifiedArchiveTable(tenant, target.ToString());
        var columns = "\"generation\", \"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"value\"";
        Assert.Equal(await RowsAsync(sourceTable, "1, 2, 4", columns), await RowsAsync(targetTable, "1, 2, 4", columns));
        var mapColumns = "\"range_start\", \"range_end\", \"rtid_scope\", \"generation\"";
        Assert.Equal(await RowsAsync(GenerationMapSqlBuilder.GenMapTable(tenant, source.ToString()), "1, 2, 3", mapColumns),
            await RowsAsync(GenerationMapSqlBuilder.GenMapTable(tenant, target.ToString()), "1, 2, 3", mapColumns));
    }

    private async Task<(ArchiveCopyPlan Plan, ArchiveCopyStagingLocation Location)> DumpAsync(
        string tenant, ArchiveCopyTarget source, int targetSliceRows = 400)
    {
        var options = new ArchiveCopyOptions { TargetSliceRows = targetSliceRows };
        var runner = new StagingCopyRunner(NullLogger.Instance, tenant, fixture.Client);
        var location = CrateStagingCopyFixture.NewFileLocation();
        var plan = await runner.PlanAsync(source, options, Ct);
        await fixture.PrepareDirectoriesAsync(plan, location);
        var dumped = await Collect(runner.CopyToAsync(source, plan, location, options, Ct));
        Assert.All(dumped, r => Assert.True(r.IsSuccess, r.Message));
        return (plan, location);
    }

    /// <summary>
    ///     Reports a circuit breaker failure for a <c>COPY</c> that contains <c>marker</c>; with <c>execute</c> the statement ran first,
    ///     so its rows are in the table when the failure is reported.
    /// </summary>
    private sealed class FailAfterExecutingClient(
        ICrateCopyClient inner, string marker, int failures, bool execute = true, bool oncePerTable = false) : ICrateCopyClient
    {
        private readonly HashSet<bool> _failedTables = [];

        public int Failed { get; private set; }

        public List<string> Loads { get; } = [];

        public async Task<CrateStatementResult> ExecuteStatementAsync(
            string tenantId, CopyStatement statement, TimeSpan commandTimeout, CancellationToken cancellationToken)
        {
            var sql = statement.Reveal();
            var isLoad = statement.IsCopy && sql.Contains(" " + marker + " ", StringComparison.Ordinal);
            if (isLoad)
            {
                Loads.Add(sql);
            }

            var isMap = sql.Contains("__genmap", StringComparison.Ordinal);
            var due = isLoad && Failed < failures && (!oncePerTable || !_failedTables.Contains(isMap));
            if (!due)
            {
                return await inner.ExecuteStatementAsync(tenantId, statement, commandTimeout, cancellationToken);
            }

            _failedTables.Add(isMap);
            Failed++;
            if (execute)
            {
                await inner.ExecuteStatementAsync(tenantId, statement, commandTimeout, cancellationToken);
            }

            throw new StreamDataCopyStatementException(statement.Redacted, "XX000", BreakerText);
        }
    }

    private sealed class InstantClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            callback(state);
            return new Done();
        }

        private sealed class Done : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
