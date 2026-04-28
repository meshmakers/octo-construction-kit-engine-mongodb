using Dapper;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dapper;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Polly;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

/// <summary>
/// Client for interacting with the stream data database.
/// </summary>
internal class CrateDatabaseClient : IStreamDataDatabaseClient, IStreamDataDatabaseManagementClient,
    IStreamDataHealthCheckClient
{
    private readonly ILogger<CrateDatabaseClient> _logger;
    private readonly ICrateDbConnectionAccess _connectionAccess;
    private readonly StreamDataConfiguration _configuration;
    private readonly ResiliencePipeline _resilience;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="connectionAccess"></param>
    /// <param name="configuration"></param>
    /// <param name="resilienceOptions">Tunables for the timeout/retry/circuit-breaker pipeline (concept §8 T13).</param>
    public CrateDatabaseClient(ILogger<CrateDatabaseClient> logger, ICrateDbConnectionAccess connectionAccess,
        IOptions<StreamDataConfiguration> configuration,
        IOptions<CrateResilienceOptions> resilienceOptions)
    {
        _logger = logger;
        _connectionAccess = connectionAccess;
        _configuration = configuration.Value;
        _resilience = CrateResiliencePipeline.Build(resilienceOptions.Value);

        SqlMapper.AddTypeHandler(new JsonTypeHandler<Dictionary<string, object>>());
        SqlMapper.AddTypeHandler(new CkIdTypeHandler());
        SqlMapper.AddTypeHandler(new OctoIdTypeHandler());
    }

    public async Task<List<DataPointDto>> GetDataAsync(string tenantId, string query)
    {
        return await _resilience.ExecuteAsync(async _ => await GetDataInternalAsync(tenantId, query));
    }

    private async Task<List<DataPointDto>> GetDataInternalAsync(string tenantId, string query)
    {
        await using var connection = CreateConnection(tenantId);

        var queryResult = await connection.QueryAsync(query);

        var dataPointDtos = new List<DataPointDto>();

        foreach (var entry in queryResult)
        {
            if (entry is not IDictionary<string, object?> result)
            {
                continue;
            }

            var dp = new DataPointDto(result.ToDictionary());

            if (result.TryGetValue(Constants.Timestamp, out var timestamp))
            {
                dp.Timestamp = (DateTime)timestamp!;
            }
            else if (result.TryGetValue("T", out var ts))
            {
                dp.Timestamp = (DateTime)ts!;
            }

            if (result.TryGetValue(Constants.RtId, out var rtIdValue) &&
                OctoObjectId.TryParse(rtIdValue as string ?? "", out var octoRtId))
            {
                dp.RtId = octoRtId;
            }

            if (result.TryGetValue(Constants.CkTypeId, out var ckTypeIdValue))
            {
                var typeId = new RtCkId<CkTypeId>(ckTypeIdValue as string ?? "");
                dp.CkTypeId = typeId;
            }
            
            if(result.TryGetValue(Constants.RtWellKnownName, out var rtWellKnownName))
            {
                dp.RtWellKnownName = rtWellKnownName as string;
            }
            
            if(result.TryGetValue(Constants.RtChangedDateTime, out var rtChangedDateTime))
            {
                dp.RtChangedDateTime = (DateTime)rtChangedDateTime!;
            }
            
            if(result.TryGetValue(Constants.RtCreationDateTime, out var rtCreationDateTime))
            {
                dp.RtCreationDateTime = (DateTime)rtCreationDateTime!;
            }

            dataPointDtos.Add(dp);
        }

        return dataPointDtos;
    }

    public async Task<long> GetCountAsync(string tenantId, string countQuery)
    {
        return await _resilience.ExecuteAsync(async _ =>
        {
            await using var connection = CreateConnection(tenantId);
            return await connection.ExecuteScalarAsync<long>(countQuery);
        });
    }

    public async Task InsertDataAsync(string tenantId, IEnumerable<DataPointDto> datapoints)
    {
        var d = datapoints.ToArray();
        await _resilience.ExecuteAsync(async _ => await InsertDataInternalAsync(tenantId, d));
    }

    private async Task InsertDataInternalAsync(string tenantId, DataPointDto[] d)
    {
        await using var connection = CreateConnection(tenantId);

        var command = new NpgsqlCommand(
            string.Format(Queries.InsertStreamDataBulk, TenantSchema.QualifiedLegacyTable(tenantId)),
            connection);


        var dataParameter = new NpgsqlParameter("@data", NpgsqlDbType.Array | NpgsqlDbType.Json)
        {
            Value = d.Select(x => x.Attributes).ToArray()
        };

        command.Parameters.Add(new NpgsqlParameter<string[]>("@rtIds",
            d.Select(x => x.RtId.ToString()!).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<string[]>("@ckTypeIds",
            d.Select(x => x.CkTypeId!.ToString()).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<DateTime[]>("@timestamps",
            d.Select(x => x.Timestamp).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<string?[]>("@rtWellKnownNames",
            d.Select(x => x.RtWellKnownName).ToArray()));
        command.Parameters.Add(dataParameter);

        await command.ExecuteNonQueryAsync();
    }

    public async Task InsertDataAsync(string tenantId, DataPointDto datapoint)
    {
        await _resilience.ExecuteAsync(async _ =>
        {
            var query = string.Format(Queries.InsertStreamDataEntry, TenantSchema.QualifiedLegacyTable(tenantId));
            await using var connection = CreateConnection(tenantId);

            var data = new Json<Dictionary<string, object?>>(datapoint.Attributes.ToDictionary());

            await connection.ExecuteAsync(query,
                new
                {
                    datapoint.RtId,
                    datapoint.CkTypeId,
                    datapoint.Timestamp,
                    data
                });
        });
    }

    public async Task CreateStreamDataTableIfNotExistAsync(string tenantId)
    {
        await _resilience.ExecuteAsync(async _ =>
        {
            await using var connection = CreateConnection(tenantId);

            var replicaClause = _configuration.NumberOfReplicas >= 0
                ? $"WITH (number_of_replicas = {_configuration.NumberOfReplicas})"
                : "";

            await connection.ExecuteAsync(
                string.Format(
                    Queries.CreateTableIfNotExists,
                    TenantSchema.QualifiedLegacyTable(tenantId),
                    _configuration.NumberOfShards,
                    replicaClause));
        });
    }

    public async Task RefreshLegacyTableAsync(string tenantId)
    {
        await _resilience.ExecuteAsync(async _ =>
        {
            await using var connection = CreateConnection(tenantId);
            await connection.ExecuteAsync(
                string.Format(Queries.RefreshTable, TenantSchema.QualifiedLegacyTable(tenantId)));
        });
    }

    public async Task<IReadOnlyList<string>> ListTablesInTenantSchemaAsync(string tenantId)
    {
        return await _resilience.ExecuteAsync(async _ =>
        {
            var schema = TenantSchema.SchemaName(tenantId);
            await using var connection = CreateConnection(tenantId);
            var rows = await connection.QueryAsync<string>(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema",
                new { schema });
            return (IReadOnlyList<string>)rows.AsList();
        });
    }

    public async Task DeleteStreamDataDatabaseAsync(string tenantId)
    {
        await _resilience.ExecuteAsync(async _ =>
        {
            await using var connection = CreateConnection(tenantId);

            // CrateDB has no explicit DROP SCHEMA. Dropping every table this project owns inside the
            // tenant schema (currently just the legacy stream-data table) is sufficient: CrateDB
            // implicitly drops the schema once its last table is gone.
            await connection.ExecuteAsync(
                string.Format(Queries.DeleteTableIfExists, TenantSchema.QualifiedLegacyTable(tenantId)));
        });
    }

    private NpgsqlConnection CreateConnection(string tenantId)
    {
        return _connectionAccess.CreateConnection(tenantId);
    }

    public async Task<HealthCheckResult> CheckHealthAsync()
    {
        try
        {
            await using var connection = CreateConnection("default");
            await connection.ExecuteAsync("SELECT 1");
            return HealthCheckResult.Healthy("CrateDB is healthy");
        }
        catch (Exception ex)
        {
            _logger.LogError("CrateDB is unhealthy: {Message}", ex.Message);
            return HealthCheckResult.Unhealthy("CrateDB is unhealthy");
        }
    }
}