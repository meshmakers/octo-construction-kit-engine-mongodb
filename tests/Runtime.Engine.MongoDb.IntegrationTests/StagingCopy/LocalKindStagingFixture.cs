using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     The CrateDB and the S3 store of the local kind infrastructure (octo-tools, AB#6967) for the tests that need
///     the real store. Nothing is started or changed: when the kind cluster, its CrateDB or its store is not there,
///     <see cref="Available" /> is false and the tests are skipped. The tests create tables in a schema of their own
///     and objects under a prefix of their own, and remove both.
/// </summary>
public sealed class LocalKindStagingFixture : IAsyncLifetime
{
    private const string ConnectionStringValue = "Host=localhost;Port=5432;Username=crate;SSL Mode=Prefer";

    internal CrateDatabaseClient Client { get; private set; } = null!;

    internal IOptions<StreamDataConfiguration> Configuration { get; private set; } = null!;

    public LocalS3Store Store { get; private set; } = null!;

    public bool Available { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        Store = await LocalS3Store.ConnectAsync();
        if (!Store.Available)
        {
            Reason = Store.Reason;
            return;
        }

        try
        {
            await ScalarAsync("SELECT 1");
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
        {
            Reason = "the CrateDB of the kind cluster does not answer on localhost:5432";
            return;
        }

        Configuration = Options.Create(new StreamDataConfiguration { ConnectionString = ConnectionStringValue });
        Client = new CrateDatabaseClient(
            NullLogger<CrateDatabaseClient>.Instance,
            new CrateDbConnectionAccess(Configuration, NullLogger<CrateDbConnectionAccess>.Instance),
            Configuration,
            Options.Create(new CrateResilienceOptions()),
            Options.Create(new StreamDataInstanceConfiguration()),
            Options.Create(new CrateRecomputeOptions()));
        Available = true;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionStringValue + ";Pooling=false");
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql) { Connection = connection, CommandTimeout = 3600 };
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionStringValue + ";Pooling=false");
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql) { Connection = connection, CommandTimeout = 3600 };
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

    public async Task<object?> ScalarAsync(string sql)
        => (await QueryAsync(sql)).Single().Values.Single();

    /// <summary>A repository over the kind CrateDB for the given archives; every archive is windowed (time-range / rollup storage).</summary>
    internal CrateDbStreamDataRepository NewRepository(string tenantId, bool windowed, params OctoObjectId[] archiveRtIds)
    {
        var store = A.Fake<IArchiveRuntimeStore>();
        foreach (var rt in archiveRtIds)
        {
            A.CallTo(() => store.GetAsync(rt)).Returns(new ArchiveSnapshot(rt,
                new RtCkId<CkTypeId>("Test", new CkTypeId("Sensor")), CkArchiveStatus.Disabled, "copy",
                new[] { new CkArchiveColumnSpec("Value", true, false) }) { IsTimeRange = windowed });
        }

        return new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), Client, Client, Configuration, tenantId, store, A.Fake<IFormulaEngine>());
    }
}

[CollectionDefinition(Name)]
public class LocalKindStagingCollection : ICollectionFixture<LocalKindStagingFixture>
{
    public const string Name = "LocalKindStaging";
}
