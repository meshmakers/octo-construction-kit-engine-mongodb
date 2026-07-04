using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Provides data access to a stream data database. After T17 every operation targets a per-archive
/// table identified by its schema-qualified name; the legacy single-tenant table is gone.
/// </summary>
public interface IStreamDataDatabaseClient
{
    /// <summary>
    /// Inserts a single datapoint into the per-archive CrateDB table identified by
    /// <paramref name="qualifiedTable"/>. Only columns listed in <paramref name="userColumnNames"/>
    /// are considered for the user-defined portion of the schema; standard columns are always
    /// emitted. Attributes whose camelCase column name isn't in <paramref name="userColumnNames"/>
    /// are silently dropped on the data plane (they would have been rejected at activation time
    /// during DDL generation).
    /// </summary>
    Task InsertDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames, DataPointDto datapoint);

    /// <summary>
    /// Bulk variant of <see cref="InsertDataAsync(string, string, IReadOnlyList{string}, DataPointDto)"/>.
    /// </summary>
    Task InsertDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames, IEnumerable<DataPointDto> datapoints);

    /// <summary>
    /// Inserts time-range data points into a <c>TimeRangeArchive</c> table. Schema is the
    /// <c>(window_start, window_end, rtid, ckTypeId)</c>-keyed variant emitted by
    /// <see cref="ArchiveDdlGenerator.GenerateCreateTimeRangeTable"/>; ON CONFLICT on the natural
    /// key overwrites user columns and flips <c>was_updated</c> to TRUE.
    /// </summary>
    Task InsertTimeRangeDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames, IEnumerable<TimeRangeDataPointDto> datapoints);

    /// <summary>
    /// Get data from the stream data database.
    /// </summary>
    Task<List<DataPointDto>> GetDataAsync(string tenantId, string query);

    /// <summary>
    /// Streams the raw rows of an arbitrary read query without buffering the whole result set in
    /// memory. Each row is yielded as a case-preserving dictionary of physical CrateDB column name →
    /// value, exactly as the driver returns it. Used by the archive-data export path (AB#4230) which
    /// drives keyset pagination at the caller and needs the physical columns verbatim (no DTO
    /// projection). The connection is held open for the duration of the enumeration.
    /// </summary>
    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRawRowsAsync(
        string tenantId, string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a COUNT query and returns the total number of matching rows.
    /// </summary>
    Task<long> GetCountAsync(string tenantId, string countQuery);

    /// <summary>
    /// Executes a non-query SQL statement (INSERT / UPDATE / DELETE / upsert) and returns the
    /// number of affected rows. Used by the rollup orchestrator for the bucket-aggregation upsert
    /// (rollup-archives concept §5).
    /// </summary>
    Task<int> ExecuteNonQueryAsync(string tenantId, string sql, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes one parameterised non-query statement once per argument set in a single CrateDB
    /// bulk operation (one prepared statement, one round-trip). Used by the computed-column backfill
    /// to write a whole page's rows at once — CrateDB executes the bulk far cheaper than N individual
    /// statements. Positional parameters (<c>$1, $2, …</c>) bind each set in order.
    /// </summary>
    Task ExecuteBulkAsync(string tenantId, string parameterizedSql,
        IReadOnlyList<IReadOnlyList<object?>> argumentSets, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces CrateDB to apply pending inserts to the read path immediately for the given archive
    /// table. CrateDB applies inserts asynchronously (~1s default) so callers that need strict
    /// read-after-write consistency must invoke this after a bulk insert. Concept §15: callers opt
    /// in; the repository does NOT call it after every insert because it is expensive under load.
    /// </summary>
    Task RefreshArchiveTableAsync(string tenantId, string qualifiedTable);

    /// <summary>
    /// Returns per-table storage stats (row count, on-disk size, health) for every entry in
    /// <paramref name="tableNames"/> that exists in the tenant's schema. Tables not found in
    /// <c>sys.shards</c> are omitted from the result — callers handle the "not provisioned yet"
    /// case at the next layer up. One round-trip per call; CrateDB's <c>sys.shards</c> +
    /// <c>sys.health</c> are cheap to query under load.
    /// </summary>
    Task<IReadOnlyList<Dtos.CrateTableStatsRow>> GetTableStatsAsync(
        string tenantId,
        IReadOnlyList<string> tableNames,
        CancellationToken cancellationToken = default);
}