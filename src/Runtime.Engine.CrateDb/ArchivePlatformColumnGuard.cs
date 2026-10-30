using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Makes sure an archive table carries its platform columns before a statement names them: the row origin
/// columns of a raw or time-range table (AB#5356), the manual columns of a rollup table (AB#7727). A table
/// provisioned before they existed gets them through <c>ALTER TABLE … ADD COLUMN</c>.
/// </summary>
/// <remarks>
/// Checked once per table and process (<see cref="RecomputeProcessState.EnsuredOriginColumns" />). Several
/// service replicas may add the same column at the same moment; the loser's "already has a column" is benign.
/// The ALTER is a mapping change in the cluster state, so it does not wait for and does not disturb a statement
/// that is running on the table: such a statement simply does not name the column, and its rows hold NULL, which
/// every reader takes as "no manual values".
/// </remarks>
internal sealed class ArchivePlatformColumnGuard(
    string tenantId,
    IStreamDataDatabaseClient databaseClient,
    IStreamDataDatabaseManagementClient managementClient,
    RecomputeProcessState state,
    ILogger logger)
{
    /// <summary>Ensures the platform columns <paramref name="archive" /> has by its kind.</summary>
    public Task EnsureAsync(ArchiveSnapshot archive, CancellationToken cancellationToken)
        => archive.RollupAggregations is null
            ? EnsureOriginColumnsAsync(archive.RtId.ToString(), cancellationToken)
            : EnsureRollupColumnsAsync(archive.RtId.ToString(), cancellationToken);

    /// <summary>Ensures the row origin columns of the raw or time-range table of <paramref name="archiveRtId" />.</summary>
    public Task EnsureOriginColumnsAsync(string archiveRtId, CancellationToken cancellationToken)
        => EnsureAsync(archiveRtId, ArchiveRowOriginColumns.Columns, cancellationToken);

    /// <summary>Ensures the manual columns of the rollup table of <paramref name="archiveRtId" />.</summary>
    public Task EnsureRollupColumnsAsync(string archiveRtId, CancellationToken cancellationToken)
        => EnsureAsync(archiveRtId, ArchiveRowOriginColumns.RollupColumns, cancellationToken);

    /// <summary>
    /// Reads the physical column names of an archive table from <c>information_schema</c>. An empty
    /// result means "table not found or not readable".
    /// </summary>
    public async Task<IReadOnlySet<string>> ReadPhysicalColumnNamesAsync(string archiveRtId)
    {
        var schemaName = TenantSchema.SchemaName(tenantId);
        var tableName = "archive_" + archiveRtId;
        var query =
            "SELECT column_name FROM information_schema.columns " +
            $"WHERE table_schema = '{schemaName.Replace("'", "''")}' " +
            $"AND table_name = '{tableName.Replace("'", "''")}'";

        var names = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var row in databaseClient.StreamRawRowsAsync(tenantId, query))
        {
            if (row.TryGetValue("column_name", out var value) && value is string name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private async Task EnsureAsync(
        string archiveRtId, IReadOnlyList<(string Name, string Definition)> columns, CancellationToken cancellationToken)
    {
        var qualifiedTable = TenantSchema.QualifiedArchiveTable(tenantId, archiveRtId);
        if (state.EnsuredOriginColumns.ContainsKey(qualifiedTable))
        {
            return;
        }

        var existing = await ReadPhysicalColumnNamesAsync(archiveRtId);
        if (existing.Count == 0)
        {
            // Table not found or catalogue not readable: let the statement report the real problem, and ask again
            // next time instead of remembering an answer that was never given.
            return;
        }

        foreach (var (name, definition) in columns)
        {
            if (existing.Contains(name))
            {
                continue;
            }

            logger.LogInformation(
                "Archive {Table}: adding platform column {Column}, which the table does not have yet.",
                qualifiedTable, name);
            try
            {
                await managementClient.ExecuteDdlAsync(tenantId,
                    $"ALTER TABLE {qualifiedTable} ADD COLUMN \"{name}\" {definition};", cancellationToken);
            }
            catch (Exception ex) when (CrateDbStreamDataRepository.IsColumnAlreadyExists(ex))
            {
                logger.LogDebug(ex, "Archive {Table}: platform column {Column} was added concurrently.",
                    qualifiedTable, name);
            }
        }

        state.EnsuredOriginColumns[qualifiedTable] = true;
    }
}
