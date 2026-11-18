using System.Globalization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     One single-node CrateDB for the staging copy tests (AB#5967, slice AB#6967). The tests copy to and from
///     <c>file://</c> in the container's own file system (concept §12, decision 4), so no object store is needed;
///     the credential tests use an S3 URI that points nowhere. The collection runs its tests one after the other,
///     which the cluster setting <c>stats.jobs_log_filter</c> that some of them change requires.
/// </summary>
public sealed class CrateStagingCopyFixture : IAsyncLifetime
{
    /// <summary>
    ///     The CrateDB image of the fixture; <c>OCTO_TEST_CRATEDB_IMAGE</c> (for example <c>crate:5.10.10</c>) overrides the
    ///     default so the same tests can run on the 5.x line that is still in production during a rolling upgrade (AB#7149).
    /// </summary>
    private static readonly string Image = Environment.GetEnvironmentVariable("OCTO_TEST_CRATEDB_IMAGE") is { Length: > 0 } image
        ? image
        : "crate:6.4.6";

    /// <summary>The version of the image: the tag after the colon.</summary>
    public static readonly string StorageVersion = Image[(Image.LastIndexOf(':') + 1)..];

    private IContainer? _container;

    internal CrateDatabaseClient Client { get; private set; } = null!;

    internal IOptions<StreamDataConfiguration> Configuration { get; private set; } = null!;

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>Name of the fixture's container, for failure messages (AB#9250).</summary>
    public string ContainerName { get; private set; } = string.Empty;

    // CrateDB rejects the ROLLBACK a pooled connection sends when it is reset; the helpers do not pool.
    private string PlainConnectionString => ConnectionString + ";Pooling=false";

    public async ValueTask InitializeAsync()
    {
        ContainerName = $"cratedb-stagingcopy-test-{Guid.NewGuid():N}";
        var container = new ContainerBuilder(Image)
            .WithName(ContainerName)
            .WithPortBinding(5432, true)
            .WithPortBinding(4200, true)
            .WithEnvironment("CRATE_HEAP_SIZE", TestContainerLimits.CrateHeapSize)
            // AB#8511: hard cap, see TestContainerLimits.
            .WithCreateParameterModifier(TestContainerLimits.Resources(
                TestContainerLimits.CrateMemoryLimitBytes, TestContainerLimits.CrateCpuLimit))
            .WithCommand("-Cdiscovery.type=single-node")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("started"))
            .Build();

        try
        {
            await container.StartAsync();
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }

        _container = container;
        ConnectionString = $"Host=localhost;Port={container.GetMappedPublicPort(5432)};Username=crate;SSL Mode=Prefer";
        Configuration = Options.Create(new StreamDataConfiguration { ConnectionString = ConnectionString });
        Client = new CrateDatabaseClient(
            NullLogger<CrateDatabaseClient>.Instance,
            new CrateDbConnectionAccess(Configuration, NullLogger<CrateDbConnectionAccess>.Instance),
            Configuration,
            Options.Create(new CrateResilienceOptions()),
            Options.Create(new StreamDataInstanceConfiguration()),
            Options.Create(new CrateRecomputeOptions()));

        // the container logged "started"; make sure it also answers before the first statement of the fixture
        await EnsureReachableAsync(CancellationToken.None);
        await SetJobLogFilterAsync(JobLogFilterGuard.RecommendedFilter);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is null)
        {
            return;
        }

        try
        {
            await _container.StopAsync();
        }
        finally
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    ///     Waits until the container accepts a connection and answers <c>SELECT 1</c>, at most
    ///     <see cref="CrateTestConnect.ReadinessBudget" /> (AB#9250). Tests that open their own pools with a short connect
    ///     timeout call it before the measured part: on a loaded machine the first connect to the container can take longer
    ///     than their timeout, and without this wait such a test failed with a bare <c>BrokenCircuitException</c> of the
    ///     resilience pipeline instead of saying that the container was not reachable.
    /// </summary>
    public async Task EnsureReachableAsync(CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Exception? last = null;
        var attempts = 0;
        // every attempt and every pause is cut to the rest of the budget, so the wait never exceeds it
        while (true)
        {
            var remaining = CrateTestConnect.ReadinessBudget - watch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            attempts++;
            var attempt = remaining < CrateTestConnect.ReadinessAttemptBudget
                ? remaining
                : CrateTestConnect.ReadinessAttemptBudget;
            last = await TryConnectOnceAsync(attempt, cancellationToken);
            if (last is null)
            {
                return;
            }

            remaining = CrateTestConnect.ReadinessBudget - watch.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                var pause = TimeSpan.FromMilliseconds(500);
                await Task.Delay(remaining < pause ? remaining : pause, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"{Describe()} did not accept a connection and answer SELECT 1 within {watch.Elapsed.TotalSeconds:0.0} s " +
            $"({attempts} attempts of at most {CrateTestConnect.ReadinessAttemptBudget.TotalSeconds:0} s); " +
            "the machine or the Docker VM is probably overloaded " +
            "(test README, \"Container resources and parallelism\"). " +
            $"Last error: {last?.GetType().Name}: {last?.Message}",
            last);
    }

    /// <summary>
    ///     One connect and <c>SELECT 1</c> on a plain connection within <paramref name="budget" />: <c>null</c> when the
    ///     container answered, otherwise the error.
    /// </summary>
    internal async Task<Exception?> TryConnectOnceAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(budget.TotalSeconds));
        var connectionString = new NpgsqlConnectionStringBuilder(PlainConnectionString)
        {
            Timeout = seconds,
            CommandTimeout = seconds,
        }.ConnectionString;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(budget);
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(attempt.Token);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(attempt.Token);
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                   ex is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            return ex;
        }
    }

    /// <summary>The container, its image and its endpoint, for failure messages.</summary>
    public string Describe()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        return $"CrateDB test container '{ContainerName}' ({Image}, {builder.Host}:{builder.Port})";
    }

    /// <summary>A tenant id that maps to a schema of its own, so tests do not see each other's tables.</summary>
    public static string NewTenantId() => "stg" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>A location in the container's file system, e.g. <c>file:///tmp/octo-staging/&lt;guid&gt;</c>.</summary>
    public static ArchiveCopyStagingLocation NewFileLocation() =>
        new($"file:///tmp/octo-staging/{Guid.NewGuid():N}");

    /// <summary>
    ///     COPY TO DIRECTORY does not create the directory on a file system (an object store has none): creates the
    ///     slice folders of <paramref name="plan" /> in the container and makes them writable for the CrateDB process.
    /// </summary>
    public async Task PrepareDirectoriesAsync(ArchiveCopyPlan plan, ArchiveCopyStagingLocation location)
    {
        var root = Path(location);
        foreach (var slice in plan.Slices)
        {
            var folder = Path(location.WithSubPath(slice.GetStagingPath(plan.ArchiveRtId)));
            var (exit, output) = await ExecAsync("sh", "-c", $"mkdir -p {folder} && chmod -R 777 {root}");
            Assert.True(exit == 0, output);
        }
    }

    /// <summary>The same for a single directory that a statement writes to directly.</summary>
    public async Task PrepareDirectoryAsync(ArchiveCopyStagingLocation location)
    {
        var folder = Path(location);
        var (exit, output) = await ExecAsync("sh", "-c", $"mkdir -p {folder} && chmod -R 777 {folder}");
        Assert.True(exit == 0, output);
    }

    /// <summary>The file system path of a <c>file://</c> location inside the container.</summary>
    public static string Path(ArchiveCopyStagingLocation location)
        => location.RevealUri().Replace("file://", string.Empty, StringComparison.Ordinal);

    public async Task SetJobLogFilterAsync(string? filter)
    {
        var value = (filter ?? "true").Replace("'", "''", StringComparison.Ordinal);
        await SqlAsync($"SET GLOBAL PERSISTENT {JobLogFilterGuard.SettingName} = '{value}'");
    }

    /// <summary>Runs a statement on a plain connection (not through the client under test).</summary>
    public async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(PlainConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs a query on a plain connection; the rows as column → value.</summary>
    public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(PlainConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public async Task<long> CountAsync(string sql)
        => Convert.ToInt64((await QueryAsync(sql)).Single().Values.Single(), CultureInfo.InvariantCulture);

    /// <summary>Runs a command inside the CrateDB container (to look at, or remove, the files of a slice).</summary>
    public async Task<(long ExitCode, string Output)> ExecAsync(params string[] command)
    {
        var result = await _container!.ExecAsync(command);
        return (result.ExitCode ?? -1, result.Stdout + result.Stderr);
    }

    /// <summary>Creates the raw archive table of <paramref name="archiveRtId"/> and fills it with hourly rows.</summary>
    internal async Task CreateRawArchiveAsync(string tenantId, OctoObjectId archiveRtId, int hourlyRows = 0, bool withValueColumn = true)
    {
        var table = TenantSchema.QualifiedArchiveTable(tenantId, archiveRtId.ToString());
        var columns = withValueColumn
            ? new[] { new ArchiveColumnDdl("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false) }
            : Array.Empty<ArchiveColumnDdl>();
        await SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(table, columns, numberOfShards: 2, numberOfReplicas: 0));

        if (hourlyRows > 0)
        {
            // 2026-01-01 00:00 UTC in epoch milliseconds, one row an hour.
            await SqlAsync(
                $"INSERT INTO {table} (\"rtid\", \"timestamp\", \"cktypeid\", \"value\") " +
                "SELECT 'rt1', CAST(1767225600000 + CAST(generate_series AS BIGINT) * 3600000 AS TIMESTAMP WITH TIME ZONE), 'Test/Sensor-1', generate_series * 1.5 " +
                $"FROM generate_series(0, {hourlyRows - 1})");
            await SqlAsync($"REFRESH TABLE {table}");
        }
    }

    /// <summary>The first window of the rollups the tests create: 2026-01-01 00:00 UTC.</summary>
    internal static readonly DateTime RollupOrigin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const long RollupOriginMs = 1767225600000;

    /// <summary>
    ///     Creates the data table and the generation map of a rollup. With <paramref name="generations" /> &gt; 0 it also
    ///     fills them: 480 hourly windows at generation 0, and for every generation g in 1..n a day (24 windows) at
    ///     generation g with its pointer entry. The superseded generation-0 rows of those days stay, as they do
    ///     while the sweep of a recompute is deferred. The pointer of the last generation is scoped to one entity.
    /// </summary>
    internal async Task CreateRollupAsync(string tenantId, OctoObjectId rollupRtId, int generations = 0)
    {
        var table = TenantSchema.QualifiedArchiveTable(tenantId, rollupRtId.ToString());
        var genMap = GenerationMapSqlBuilder.GenMapTable(tenantId, rollupRtId.ToString());
        await SqlAsync(ArchiveDdlGenerator.GenerateCreateWindowedTable(table,
            [new ArchiveColumnDdl("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)],
            numberOfShards: 2, numberOfReplicas: 0, includeGeneration: true));
        await SqlAsync(GenerationMapSqlBuilder.BuildCreateTable(genMap));
        if (generations == 0)
        {
            return;
        }

        await InsertRollupRowsAsync(table, generation: 0, firstHour: 0, lastHour: 479, valueBase: 0);
        for (var g = 1; g <= generations; g++)
        {
            var firstHour = 24 * (g - 1);
            await InsertRollupRowsAsync(table, g, firstHour, firstHour + 23, valueBase: 1000 * g);
            await SqlAsync(GenerationMapSqlBuilder.BuildUpsertPointer(genMap,
                RollupOrigin.AddHours(firstHour), RollupOrigin.AddHours(firstHour + 24),
                g == generations ? "rt1" : GenerationMapSqlBuilder.AllRtIdsScope, g));
        }

        await SqlAsync($"REFRESH TABLE {table}");
        await SqlAsync($"REFRESH TABLE {genMap}");
    }

    private Task InsertRollupRowsAsync(string table, long generation, int firstHour, int lastHour, int valueBase)
        => SqlAsync(
            $"INSERT INTO {table} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"was_updated\", \"generation\", \"value\") " +
            $"SELECT CAST({RollupOriginMs} + CAST(generate_series AS BIGINT) * 3600000 AS TIMESTAMP WITH TIME ZONE), " +
            $"CAST({RollupOriginMs} + (CAST(generate_series AS BIGINT) + 1) * 3600000 AS TIMESTAMP WITH TIME ZONE), " +
            $"'rt1', 'Test/Sensor-1', FALSE, {generation}, {valueBase} + generate_series * 1.5 FROM generate_series({firstHour}, {lastHour})");

    /// <summary>A repository over this CrateDB for the rollup <paramref name="rollupRtId" /> of <paramref name="tenantId" />.</summary>
    internal CrateDbStreamDataRepository NewRollupRepository(string tenantId, OctoObjectId rollupRtId)
    {
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(rollupRtId)).Returns(new ArchiveSnapshot(rollupRtId,
            new RtCkId<CkTypeId>("Test", new CkTypeId("Sensor")), CkArchiveStatus.Activated, "rollup",
            new[] { new CkArchiveColumnSpec("Value", true, false) })
        {
            RollupAggregations = [new CkRollupAggregationSpec("Value", CkRollupFunction.Sum, null)],
            Period = TimeSpan.FromHours(1)
        });

        return new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), Client, Client, Configuration, tenantId, store, A.Fake<IFormulaEngine>());
    }

    /// <summary>
    ///     A repository over this CrateDB that also knows the rollups in <paramref name="rollups" /> (for the validator and the
    ///     repair, which enumerate rollups) and the raw <paramref name="sources" /> they aggregate. Each process of a host has its own
    ///     <see cref="RecomputeProcessState" />; pass one to share it, none for a "restarted" process.
    /// </summary>
    internal CrateDbStreamDataRepository NewReconcileRepository(
        string tenantId, IReadOnlyList<RollupArchiveSnapshot> rollups, IReadOnlyList<OctoObjectId> sources,
        RecomputeProcessState? state = null, IOptions<CrateRecomputeOptions>? options = null, IRollupArchiveRuntimeStore? rollupStore = null,
        IStreamDataDatabaseClient? databaseClient = null)
    {
        var archives = A.Fake<IArchiveRuntimeStore>();
        var type = new RtCkId<CkTypeId>("Test", new CkTypeId("Sensor"));
        foreach (var source in sources)
        {
            A.CallTo(() => archives.GetAsync(source)).Returns(new ArchiveSnapshot(source, type, CkArchiveStatus.Activated, "raw",
                new[] { new CkArchiveColumnSpec("Value", true, false) }));
        }

        var specs = new[] { new CkRollupAggregationSpec("Value", CkRollupFunction.Sum, null) };
        foreach (var rollup in rollups)
        {
            A.CallTo(() => archives.GetAsync(rollup.RtId)).Returns(new ArchiveSnapshot(rollup.RtId, type, CkArchiveStatus.Activated, "rollup",
                RollupColumnGenerator.Generate(specs))
            {
                RollupAggregations = specs,
                Period = TimeSpan.FromHours(1)
            });
        }

        var store = rollupStore ?? A.Fake<IRollupArchiveRuntimeStore>();
        if (rollupStore is null)
        {
            A.CallTo(() => store.EnumerateAsync()).ReturnsLazily(() => ToAsync(rollups));
            foreach (var rollup in rollups)
            {
                A.CallTo(() => store.GetAsync(rollup.RtId)).Returns(rollup);
            }
        }

        return new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), databaseClient ?? Client, Client, Configuration, tenantId, archives, A.Fake<IFormulaEngine>(),
            store, recomputeStateStore: null, archiveAuditTrail: null, options, state);
    }

    private static async IAsyncEnumerable<RollupArchiveSnapshot> ToAsync(IReadOnlyList<RollupArchiveSnapshot> rollups)
    {
        await Task.CompletedTask;
        foreach (var rollup in rollups)
        {
            yield return rollup;
        }
    }

    /// <summary>A repository over this CrateDB for <paramref name="archiveRtIds"/> of <paramref name="tenantId"/>.</summary>
    internal CrateDbStreamDataRepository NewRepository(string tenantId, params OctoObjectId[] archiveRtIds)
    {
        var store = A.Fake<IArchiveRuntimeStore>();
        foreach (var rt in archiveRtIds)
        {
            A.CallTo(() => store.GetAsync(rt)).Returns(new ArchiveSnapshot(rt,
                new RtCkId<CkTypeId>("Test", new CkTypeId("Sensor")), CkArchiveStatus.Activated, "raw",
                new[] { new CkArchiveColumnSpec("Value", true, false) }));
        }

        return new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), Client, Client, Configuration, tenantId, store, A.Fake<IFormulaEngine>());
    }
}
