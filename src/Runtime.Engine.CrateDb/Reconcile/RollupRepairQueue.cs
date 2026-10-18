using System.Globalization;
using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Reconcile;

/// <summary>
/// The durable queue of rollup repairs (<see cref="IStreamDataRepository.EnqueueRollupRepairAsync"/>, AB#6968): one row per request in
/// the schema's table <c>__rollup_repair</c>, keyed by tenant id and request id. The host's recompute tick claims the pending
/// requests and runs them, so a repair neither runs under the request that asked for it nor is lost by a restart.
/// </summary>
/// <remarks>
/// Every column is written explicitly and none has a default (AB#6647); "not yet" is 0 / empty text, not NULL. A request left
/// <c>Running</c> longer than <see cref="RunningTimeout"/> belongs to a process that died and is claimed again - the repair is idempotent.
/// A claim is an <c>UPDATE … WHERE state = &lt;seen&gt;</c>; two processes claiming at once can both win, which costs a second
/// (idempotent) run, not a wrong result.
/// </remarks>
internal sealed class RollupRepairQueue(
    string tenantId,
    IStreamDataDatabaseClient databaseClient,
    IStreamDataDatabaseManagementClient managementClient,
    TimeProvider clock,
    ILogger logger)
{
    public static readonly TimeSpan RunningTimeout = TimeSpan.FromMinutes(30);
    public const string TableName = "__rollup_repair";

    public string QualifiedTable => $"\"{TenantSchema.SchemaName(tenantId)}\".\"{TableName}\"";

    internal static string BuildCreateTable(string table) =>
        $"CREATE TABLE IF NOT EXISTS {table} (" +
        " \"tenant_id\" TEXT NOT NULL, \"request_id\" TEXT NOT NULL, \"state\" TEXT NOT NULL," +
        " \"requested_at_ms\" BIGINT NOT NULL, \"started_at_ms\" BIGINT NOT NULL, \"finished_at_ms\" BIGINT NOT NULL," +
        " \"rollups\" TEXT NOT NULL, \"planned\" TEXT NOT NULL, \"report\" TEXT NOT NULL," +
        " PRIMARY KEY (\"tenant_id\", \"request_id\")) CLUSTERED INTO 1 SHARDS;";

    private static string Lit(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string BuildInsert(string table, string tenant, string requestId, long requestedMs, string rollups, string planned) =>
        $"INSERT INTO {table} (\"tenant_id\", \"request_id\", \"state\", \"requested_at_ms\", \"started_at_ms\", \"finished_at_ms\", \"rollups\", \"planned\", \"report\") " +
        $"VALUES ({Lit(tenant)}, {Lit(requestId)}, 'Pending', {Num(requestedMs)}, 0, 0, {Lit(rollups)}, {Lit(planned)}, '');";

    internal static string BuildSelect(string table, string tenant, string? requestId) =>
        "SELECT \"request_id\", \"state\", \"requested_at_ms\", \"started_at_ms\", \"finished_at_ms\", \"rollups\", \"planned\", \"report\", \"_seq_no\", \"_primary_term\" " +
        $"FROM {table} WHERE \"tenant_id\" = {Lit(tenant)}" + (requestId is null ? string.Empty : $" AND \"request_id\" = {Lit(requestId)}") + ";";

    /// <summary>
    /// The claim is an optimistic-concurrency update: it applies only to the version of the row that was read (<c>_seq_no</c> and
    /// <c>_primary_term</c>), so of several replicas reading the same pending request exactly one wins.
    /// </summary>
    internal static string BuildClaim(string table, string tenant, string requestId, long seqNo, long primaryTerm, long startedMs) =>
        $"UPDATE {table} SET \"state\" = 'Running', \"started_at_ms\" = {Num(startedMs)} " +
        $"WHERE \"tenant_id\" = {Lit(tenant)} AND \"request_id\" = {Lit(requestId)} " +
        $"AND \"_seq_no\" = {Num(seqNo)} AND \"_primary_term\" = {Num(primaryTerm)};";

    internal static string BuildComplete(string table, string tenant, string requestId, string state, long finishedMs, string report) =>
        $"UPDATE {table} SET \"state\" = {Lit(state)}, \"finished_at_ms\" = {Num(finishedMs)}, \"report\" = {Lit(report)} " +
        $"WHERE \"tenant_id\" = {Lit(tenant)} AND \"request_id\" = {Lit(requestId)};";

    /// <summary>A queued request as stored; the ids of the rollups it is limited to (null = all).</summary>
    internal sealed record Row(
        RollupRepairRequest Request, IReadOnlyCollection<OctoObjectId>? Rollups, long StartedAtMs, string RawState,
        long SeqNo, long PrimaryTerm, string RollupsText);

    public async Task<RollupRepairRequest> EnqueueAsync(
        IReadOnlyCollection<OctoObjectId>? rollupRtIds, IReadOnlyList<RollupRepairAction> planned, CancellationToken cancellationToken)
    {
        var rollupsText = rollupRtIds is null
            ? string.Empty
            : JsonSerializer.Serialize(rollupRtIds.Select(r => r.ToString()).OrderBy(s => s, StringComparer.Ordinal));

        // One active request per tenant and rollup set: a second call while one is pending, or running within its lease,
        // returns that request instead of queueing a duplicate (the repair is idempotent, so the rare race of two calls in
        // the same instant costs a second run, not a wrong result).
        var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var active = (await ReadAsync(null, cancellationToken)).FirstOrDefault(r =>
            r.RollupsText == rollupsText &&
            (r.RawState == "Pending" || (r.RawState == "Running" && nowMs - r.StartedAtMs <= (long)RunningTimeout.TotalMilliseconds)));
        if (active is not null)
        {
            logger.LogInformation("A rollup repair for the same rollups of tenant {TenantId} is already active: {RequestId}.", tenantId, active.Request.RequestId);
            return active.Request;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var now = clock.GetUtcNow();
        await managementClient.ExecuteDdlAsync(tenantId, BuildCreateTable(QualifiedTable), cancellationToken);
        await databaseClient.ExecuteNonQueryAsync(tenantId,
            BuildInsert(QualifiedTable, tenantId, requestId, now.ToUnixTimeMilliseconds(),
                rollupsText, JsonSerializer.Serialize(planned)),
            CrateStatementClass.Maintenance, cancellationToken);
        await databaseClient.RefreshArchiveTableAsync(tenantId, QualifiedTable, CrateStatementClass.Maintenance, cancellationToken);
        logger.LogInformation("Rollup repair {RequestId} of tenant {TenantId} is queued with {Steps} planned step(s).", requestId, tenantId, planned.Count);
        return new RollupRepairRequest(requestId, RollupRepairRequestState.Pending, now.UtcDateTime, null, planned, null);
    }

    public async Task<RollupRepairRequest?> GetAsync(string requestId, CancellationToken cancellationToken)
        => (await ReadAsync(requestId, cancellationToken)).FirstOrDefault()?.Request;

    /// <summary>The requests that can be run now: pending, or running for longer than <see cref="RunningTimeout"/>.</summary>
    public async Task<IReadOnlyList<Row>> ClaimRunnableAsync(CancellationToken cancellationToken)
    {
        // At most one repair of a tenant runs at a time, whatever rollups the requests name: two repairs would rewind and
        // recompute the same rollup at once. Requests wait as Pending until the running one has ended or its lease expired.
        var claimed = new List<Row>();
        var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var rows = (await ReadAsync(null, cancellationToken)).OrderBy(r => r.Request.RequestedAtUtc).ThenBy(r => r.Request.RequestId, StringComparer.Ordinal).ToList();
        bool LeaseValid(Row r) => r.RawState == "Running" && nowMs - r.StartedAtMs <= (long)RunningTimeout.TotalMilliseconds;
        if (rows.Any(LeaseValid))
        {
            return claimed;
        }

        foreach (var row in rows)
        {
            var runnable = row.RawState == "Pending" || (row.RawState == "Running" && !LeaseValid(row));
            if (!runnable)
            {
                continue;
            }

            var won = await databaseClient.ExecuteNonQueryAsync(tenantId,
                BuildClaim(QualifiedTable, tenantId, row.Request.RequestId, row.SeqNo, row.PrimaryTerm, nowMs),
                CrateStatementClass.Maintenance, cancellationToken);
            if (won == 0)
            {
                continue;
            }

            await databaseClient.RefreshArchiveTableAsync(tenantId, QualifiedTable, CrateStatementClass.Maintenance, cancellationToken);

            // Two replicas may have claimed two different requests in the same instant: the one that comes first in the
            // queue order keeps running, the other puts its request back.
            var competing = (await ReadAsync(null, cancellationToken))
                .Where(r => r.Request.RequestId != row.Request.RequestId && LeaseValid(r) && r.StartedAtMs >= nowMs - 1000)
                .Any(r => string.CompareOrdinal(r.Request.RequestId, row.Request.RequestId) < 0);
            if (competing)
            {
                await databaseClient.ExecuteNonQueryAsync(tenantId, BuildRelease(QualifiedTable, tenantId, row.Request.RequestId),
                    CrateStatementClass.Maintenance, cancellationToken);
                await databaseClient.RefreshArchiveTableAsync(tenantId, QualifiedTable, CrateStatementClass.Maintenance, cancellationToken);
                break;
            }

            claimed.Add(row);
            break; // one request per tick and tenant; the next waits
        }

        return claimed;
    }

    internal static string BuildRelease(string table, string tenant, string requestId) =>
        $"UPDATE {table} SET \"state\" = 'Pending', \"started_at_ms\" = 0 WHERE \"tenant_id\" = {Lit(tenant)} AND \"request_id\" = {Lit(requestId)};";

    public async Task CompleteAsync(string requestId, RollupRepairReport report, CancellationToken cancellationToken)
    {
        var state = report.IsOk ? "Done" : "Failed";
        await databaseClient.ExecuteNonQueryAsync(tenantId,
            BuildComplete(QualifiedTable, tenantId, requestId, state, clock.GetUtcNow().ToUnixTimeMilliseconds(), JsonSerializer.Serialize(report)),
            CrateStatementClass.Maintenance, cancellationToken);
        await databaseClient.RefreshArchiveTableAsync(tenantId, QualifiedTable, CrateStatementClass.Maintenance, cancellationToken);
        logger.LogInformation("Rollup repair {RequestId} of tenant {TenantId} ended {State}{Failed}.", requestId, tenantId, state,
            report.FailedStep is null ? string.Empty : $": {report.FailedStep}");
    }

    private async Task<List<Row>> ReadAsync(string? requestId, CancellationToken cancellationToken)
    {
        var rows = new List<Row>();
        try
        {
            if (await databaseClient.GetCountAsync(tenantId, RollupConsistencySql.BuildTableExists(tenantId, TableName),
                    CrateStatementClass.Maintenance, cancellationToken) == 0)
            {
                return rows;
            }

            await databaseClient.RefreshArchiveTableAsync(tenantId, QualifiedTable, CrateStatementClass.Maintenance, cancellationToken);
            await foreach (var r in databaseClient.StreamRawRowsAsync(tenantId, BuildSelect(QualifiedTable, tenantId, requestId), cancellationToken))
            {
                var rawState = Convert.ToString(r["state"], CultureInfo.InvariantCulture) ?? "Pending";
                var finishedMs = Convert.ToInt64(r["finished_at_ms"], CultureInfo.InvariantCulture);
                var reportText = Convert.ToString(r["report"], CultureInfo.InvariantCulture) ?? string.Empty;
                var rollupsText = Convert.ToString(r["rollups"], CultureInfo.InvariantCulture) ?? string.Empty;
                var request = new RollupRepairRequest(
                    Convert.ToString(r["request_id"], CultureInfo.InvariantCulture) ?? string.Empty,
                    Enum.TryParse<RollupRepairRequestState>(rawState, out var state) ? state : RollupRepairRequestState.Pending,
                    DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(r["requested_at_ms"], CultureInfo.InvariantCulture)).UtcDateTime,
                    finishedMs == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(finishedMs).UtcDateTime,
                    JsonSerializer.Deserialize<List<RollupRepairAction>>(Convert.ToString(r["planned"], CultureInfo.InvariantCulture) ?? "[]") ?? [],
                    reportText.Length == 0 ? null : JsonSerializer.Deserialize<RollupRepairReport>(reportText));
                var rollups = rollupsText.Length == 0
                    ? null
                    : (IReadOnlyCollection<OctoObjectId>)JsonSerializer.Deserialize<List<string>>(rollupsText)!.Select(OctoObjectId.Parse).ToList();
                rows.Add(new Row(request, rollups, Convert.ToInt64(r["started_at_ms"], CultureInfo.InvariantCulture), rawState,
                    Convert.ToInt64(r["_seq_no"], CultureInfo.InvariantCulture), Convert.ToInt64(r["_primary_term"], CultureInfo.InvariantCulture),
                    rollupsText));
            }
        }
        catch (Exception ex) when (CrateDbStreamDataRepository.IsMissingTableError(ex))
        {
            // dropped meanwhile: no requests
        }

        return rows;
    }
}
