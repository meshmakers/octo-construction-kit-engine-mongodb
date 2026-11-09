using System.Globalization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataWriteLoss;

/// <summary>
///     A CrateDB with a small heap whose query circuit breaker the tests tighten (AB#8679), so that parallel multi-row archive
///     writes trip the breaker. CrateDB then ends the affected shard requests and answers the statement without an
///     error; the tests prove the client notices it. A container of its own: the setting would make every other test
///     of a shared CrateDB lose rows.
/// </summary>
public sealed class CrateMemoryPressureFixture : IAsyncLifetime
{
    private static readonly string Image = Environment.GetEnvironmentVariable("OCTO_TEST_CRATEDB_IMAGE") is { Length: > 0 } image
        ? image
        : "crate:6.4.6";

    /// <summary>Heap of the container; small, so that the breaker limit below is a few dozen MB.</summary>
    internal const string HeapSize = "512m";


    private IContainer? _container;

    internal CrateDatabaseClient Client { get; private set; } = null!;

    /// <summary>The stream-data configuration of <see cref="Client" />, for a repository over this CrateDB (AB#8680).</summary>
    internal IOptions<StreamDataConfiguration> Configuration { get; private set; } = null!;

    private string PlainConnectionString { get; set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var container = new ContainerBuilder(Image)
            .WithName($"cratedb-breaker-test-{Guid.NewGuid():N}")
            .WithPortBinding(5432, true)
            .WithEnvironment("CRATE_HEAP_SIZE", HeapSize)
            .WithCreateParameterModifier(TestContainerLimits.MemoryLimit(TestContainerLimits.CrateMemoryLimitBytes))
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
        var connectionString = $"Host=localhost;Port={container.GetMappedPublicPort(5432)};Username=crate;SSL Mode=Prefer";
        PlainConnectionString = connectionString + ";Pooling=false";
        var configuration = Options.Create(new StreamDataConfiguration { ConnectionString = connectionString });
        Configuration = configuration;
        Client = new CrateDatabaseClient(
            NullLogger<CrateDatabaseClient>.Instance,
            new CrateDbConnectionAccess(configuration, NullLogger<CrateDbConnectionAccess>.Instance),
            configuration,
            Options.Create(new CrateResilienceOptions()),
            Options.Create(new StreamDataInstanceConfiguration()),
            Options.Create(new CrateRecomputeOptions()));
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
    ///     Sets the query circuit breaker limit (share of the heap; a runtime cluster setting). The tests of the
    ///     collection run one after the other, so each sets the limit its writes need to trip the breaker.
    /// </summary>
    public Task SetQueryBreakerLimitAsync(string limit)
        => SqlAsync($"SET GLOBAL TRANSIENT \"indices.breaker.query.limit\" = '{limit}'");

    /// <summary>Back to CrateDB's default limit.</summary>
    public Task ResetQueryBreakerLimitAsync() => SqlAsync("RESET GLOBAL \"indices.breaker.query.limit\"");

    /// <summary>How often the CrateDB log says a breaker broke a request so far.</summary>
    public async Task<int> BreakerTripsAsync()
    {
        var (stdout, stderr) = await _container!.GetLogsAsync();
        return CountOccurrences(stdout) + CountOccurrences(stderr);

        static int CountOccurrences(string log)
        {
            var count = 0;
            var at = 0;
            while ((at = log.IndexOf("would be larger than configured breaker", at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at++;
            }

            return count;
        }
    }

    /// <summary>
    ///     Drops every table of a test tenant's schema. The container has a deliberately small heap, and every table a test
    ///     leaves behind keeps heap in use (AB#8680): after the stress tests of the collection the real heap usage stayed above
    ///     CrateDB's parent breaker limit (95 % of the heap), so later statements lost rows at the default limits.
    /// </summary>
    public async Task DropTenantTablesAsync(string tenantId)
    {
        var schema = TenantSchema.SchemaName(tenantId);
        var tables = await QueryAsync(
            $"SELECT table_name FROM information_schema.tables WHERE table_schema = '{schema}'");
        foreach (var row in tables)
        {
            await SqlAsync($"DROP TABLE IF EXISTS \"{schema}\".\"{row["table_name"]}\"");
        }
    }

    public async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(PlainConnectionString);
        await connection.OpenAsync();
        // The setup statements of the rollup stress (AB#8680) seed 240,000 rows into a CrateDB that may still be busy with the
        // previous test's load; the driver's 30 s default ended them.
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync();
    }

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
}

[CollectionDefinition(Name)]
public class CrateMemoryPressureCollection : ICollectionFixture<CrateMemoryPressureFixture>
{
    public const string Name = "CrateMemoryPressure";
}
