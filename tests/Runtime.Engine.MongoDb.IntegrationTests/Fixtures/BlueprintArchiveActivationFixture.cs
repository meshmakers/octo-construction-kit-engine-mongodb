using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
/// AB#7991: <see cref="BlueprintServiceFixture" /> with stream data on a REAL CrateDB - the production stream-data
/// repository, archive lifecycle and recompute orchestrator of the tenant context, so a blueprint install provisions the
/// archive tables, queues the rollup backfills and the tests drive the backfill to completion with the orchestrator tick.
/// The CrateDB container is started before the service provider is built; the connection string reaches the options
/// lazily.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public class BlueprintArchiveActivationFixture : BlueprintServiceFixture
{
    private static readonly string Image = Environment.GetEnvironmentVariable("OCTO_TEST_CRATEDB_IMAGE") is { Length: > 0 } image
        ? image
        : "crate:6.4.6";

    private IContainer? _crate;

    /// <summary>Connection string of the fixture's CrateDB.</summary>
    public string CrateConnectionString { get; private set; } = string.Empty;

    public BlueprintArchiveActivationFixture()
    {
        Services.Configure<StreamDataInstanceConfiguration>(c => c.Enabled = true);
        Services.AddSingleton<IStreamDataCkModelDescriptor>(
            _ => new StreamDataCkModelDescriptor(SystemStreamDataCkIds.CkModelId));

        // The registrations of AddCrateDbStreamDataRepository, with the connection string of the container.
        Services.Configure<StreamDataConfiguration>(c => c.ConnectionString = CrateConnectionString);
        Services.AddOptions<CrateResilienceOptions>();
        Services.AddOptions<CrateRecomputeOptions>();
        Services.AddSingleton<CrateDatabaseClient>();
        Services.AddSingleton<IStreamDataDatabaseClient>(p => p.GetRequiredService<CrateDatabaseClient>());
        Services.AddSingleton<IStreamDataDatabaseManagementClient>(p => p.GetRequiredService<CrateDatabaseClient>());
        Services.AddSingleton<IStreamDataHealthCheckClient>(p => p.GetRequiredService<CrateDatabaseClient>());
        Services.AddSingleton<ICrateDbConnectionAccess, CrateDbConnectionAccess>();
        Services.AddSingleton<IStreamDataRepositoryFactory, CrateDbStreamDataRepositoryFactory>();
        Services.AddArchiveCoverageCache();
    }

    protected override async Task InitializeServicesAsync()
    {
        var container = new ContainerBuilder(Image)
            .WithName($"cratedb-bp-archive-test-{Guid.NewGuid():N}")
            .WithPortBinding(5432, true)
            .WithEnvironment("CRATE_HEAP_SIZE", TestContainerLimits.CrateHeapSize)
            // AB#8511: hard cap, see TestContainerLimits.
            .WithCreateParameterModifier(TestContainerLimits.MemoryLimit(TestContainerLimits.CrateMemoryLimitBytes))
            .WithCommand("-Cdiscovery.type=single-node")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("started"))
            .Build();
        await container.StartAsync();
        _crate = container;
        CrateConnectionString = $"Host=localhost;Port={container.GetMappedPublicPort(5432)};Username=crate;SSL Mode=Prefer";

        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();
        var systemTenant = await systemContext.FindTenantContextAsync(systemContext.TenantId);
        await systemTenant.EnableStreamDataAsync();
    }

    protected override async Task DisposeServicesAsync()
    {
        await base.DisposeServicesAsync();
        if (_crate != null)
        {
            await _crate.DisposeAsync();
        }
    }

    /// <summary>Enables stream data on a child tenant.</summary>
    public async Task<ITenantContext> EnableStreamDataAsync(string tenantId)
    {
        var tenant = await GetSystemContext().FindTenantContextAsync(tenantId);
        await tenant.EnableStreamDataAsync();
        return tenant;
    }

    /// <summary>Runs a scalar query against the fixture's CrateDB (no pooling: CrateDB rejects the reset ROLLBACK).</summary>
    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(CrateConnectionString + ";Pooling=false");
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
