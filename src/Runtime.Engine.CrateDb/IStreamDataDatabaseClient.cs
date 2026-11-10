using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Decides the time budget of a CrateDB statement: its command timeout and the resilience
/// pipeline's timeout around it.
/// </summary>
public enum CrateStatementClass
{
    /// <summary>
    /// Statements a caller waits for (ingest, queries, lifecycle). Budget:
    /// <see cref="CrateResilienceOptions.PerAttemptTimeout"/>.
    /// </summary>
    Interactive,

    /// <summary>
    /// Statements of the rollup recompute and the rollup aggregation, which run in the background
    /// and may legitimately take longer. Budget:
    /// <see cref="Configuration.CrateRecomputeOptions.CommandTimeout"/>.
    /// </summary>
    Maintenance,
}

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
    /// <param name="conflictPrecedence">
    /// Opt-in conflict resolution (System.StreamData 1.13.0): the archive's
    /// <c>ConflictPrecedence</c>, each key naming one of <paramref name="userColumnNames" /> plus the
    /// direction of it that counts as better. Empty or null leaves the conflict update unconditional
    /// (last write wins, the historical behaviour). Otherwise competing writes are compared
    /// lexicographically in key order and the better one survives, so the stored value stops
    /// depending on the order deliveries arrive in.
    /// </param>
    /// <returns>
    /// The rows the conflict guard did not store, with the reason (AB#7598); empty without a conflict precedence.
    /// </returns>
    Task<IReadOnlyList<Client.SkippedArchiveRow>> InsertDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames, DataPointDto datapoint, IReadOnlyList<ArchiveConflictKey>? conflictPrecedence = null);

    /// <summary>
    /// Bulk variant of <see cref="InsertDataAsync(string, string, IReadOnlyList{string}, DataPointDto, IReadOnlyList{ArchiveConflictKey})"/>.
    /// </summary>
    /// <param name="conflictPrecedence">
    /// Opt-in conflict resolution (System.StreamData 1.13.0): the archive's
    /// <c>ConflictPrecedence</c>, each key naming one of <paramref name="userColumnNames" /> plus the
    /// direction of it that counts as better. Empty or null leaves the conflict update unconditional
    /// (last write wins, the historical behaviour). Otherwise competing writes are compared
    /// lexicographically in key order and the better one survives, so the stored value stops
    /// depending on the order deliveries arrive in.
    /// </param>
    /// <returns>
    /// The rows the conflict guard did not store (index into <paramref name="datapoints" />, reason; AB#7598); empty
    /// without a conflict precedence.
    /// </returns>
    Task<IReadOnlyList<Client.SkippedArchiveRow>> InsertDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames, IEnumerable<DataPointDto> datapoints, IReadOnlyList<ArchiveConflictKey>? conflictPrecedence = null);

    /// <summary>
    /// Inserts time-range data points into a windowed archive table. Schema is the
    /// <c>(window_start, window_end, rtid, ckTypeId)</c>-keyed variant emitted by
    /// <see cref="ArchiveDdlGenerator.GenerateCreateWindowedTable"/>; ON CONFLICT on the natural
    /// key overwrites user columns and flips <c>was_updated</c> to TRUE.
    /// </summary>
    /// <param name="generationTracked">
    /// True for rollup archive tables (AB#4184 Phase 6), whose primary key additionally carries the
    /// <c>generation</c> column — CrateDB requires the conflict target to name the full PK, so the
    /// insert then writes <c>generation = 0</c> explicitly and includes it in the conflict target
    /// (AB#4773: archive data import into rollup tables collapses onto the always-live generation 0).
    /// Time-range archive tables have no generation column and pass false.
    /// </param>
    /// <param name="conflictPrecedence">
    /// Opt-in conflict resolution (System.StreamData 1.13.0): the archive's
    /// <c>ConflictPrecedence</c>, each key naming one of <paramref name="userColumnNames" /> plus the
    /// direction of it that counts as better. Empty or null leaves the conflict update unconditional
    /// (last write wins, the historical behaviour). Otherwise competing writes are compared
    /// lexicographically in key order and the better one survives, so the stored value stops
    /// depending on the order deliveries arrive in.
    /// </param>
    /// <returns>
    /// The rows the conflict guard did not store (index into <paramref name="datapoints" />, reason; AB#7598); empty
    /// without a conflict precedence.
    /// </returns>
    Task<IReadOnlyList<Client.SkippedArchiveRow>> InsertTimeRangeDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames, IEnumerable<TimeRangeDataPointDto> datapoints, bool generationTracked = false, IReadOnlyList<ArchiveConflictKey>? conflictPrecedence = null);

    /// <summary>
    /// Reads <paramref name="columns" /> of the stored rows with the given primary keys (AB#8622: the inputs of a
    /// computed column a partial delivery does not send). One primary-key lookup per key, batched into a few
    /// statements; CrateDB answers them in real time, without a table refresh.
    /// </summary>
    /// <param name="windowed">True for a time-range table (key: window, rtid, cktypeid).</param>
    /// <returns>The stored rows by <see cref="Client.StoredRowKey.Value" />; a key without a row is absent.</returns>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>> ReadStoredRowsAsync(string tenantId,
        string qualifiedTable, bool windowed, IReadOnlyList<Client.StoredRowKey> keys, IReadOnlyList<string> columns);

    /// <summary>
    /// Get data from the stream data database.
    /// </summary>
    Task<List<DataPointDto>> GetDataAsync(string tenantId, string query);

    /// <summary>
    /// Streams the raw rows of an arbitrary read query without buffering the whole result set in
    /// memory. Each row is yielded as a case-preserving dictionary of physical CrateDB column name →
    /// value, exactly as the driver returns it. Used by the archive-data export path (AB#4230), which
    /// reads an archive as consecutive time slices and needs the physical columns verbatim (no DTO
    /// projection). The connection is held open for the duration of the enumeration.
    /// </summary>
    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRawRowsAsync(
        string tenantId, string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="StreamRawRowsAsync(string, string, CancellationToken)"/>, with the command
    /// timeout of <paramref name="statementClass"/> set explicitly (AB#8002). The overload without a
    /// class leaves the driver's default (the connection string's <c>CommandTimeout</c>, 30 s), which
    /// made the driver cancel a long backfill page — CrateDB reports that as
    /// <c>XX000: Job killed. Cancellation request by: &lt;user&gt;</c>. Background work passes
    /// <see cref="CrateStatementClass.Maintenance"/>. The default implementation ignores the class.
    /// </summary>
    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRawRowsAsync(
        string tenantId, string query, CrateStatementClass statementClass, CancellationToken cancellationToken) =>
        StreamRawRowsAsync(tenantId, query, cancellationToken);

    /// <summary>
    /// Executes a COUNT query and returns the total number of matching rows.
    /// </summary>
    Task<long> GetCountAsync(string tenantId, string countQuery);

    /// <summary>
    /// Executes a COUNT query and returns the total number of matching rows; the token cancels
    /// the wait for a connection and the statement. For callers that send many counts in a loop
    /// that must stay cancellable (the archive export counts every slice before reading it).
    /// </summary>
    Task<long> GetCountAsync(string tenantId, string countQuery, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a COUNT query under the time budget of <paramref name="statementClass"/> and returns
    /// the total number of matching rows. For the counts that size the statements of a background
    /// run (the slices of a rollup recompute): they belong to the same class as the statements they
    /// size, and fail the same way — <see cref="StreamDataStatementTimeoutException"/> when the
    /// budget runs out, <see cref="OperationCanceledException"/> for a cancelled token.
    /// </summary>
    Task<long> GetCountAsync(string tenantId, string countQuery, CrateStatementClass statementClass,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes a non-query SQL statement (INSERT / UPDATE / DELETE / upsert) and returns the
    /// number of affected rows. Used by the rollup orchestrator for the bucket-aggregation upsert
    /// (rollup-archives concept §5).
    /// </summary>
    Task<int> ExecuteNonQueryAsync(string tenantId, string sql, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a non-query SQL statement under the time budget of <paramref name="statementClass"/>
    /// and returns the number of affected rows. The token cancels the wait for a connection and the
    /// statement itself. A statement that runs out of its budget fails with
    /// <see cref="StreamDataStatementTimeoutException"/>, a cancelled token with
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<int> ExecuteNonQueryAsync(string tenantId, string sql, CrateStatementClass statementClass,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes one parameterised non-query statement once per argument set in a single CrateDB
    /// bulk operation (one prepared statement, one round-trip). Used by the computed-column backfill
    /// to write a whole page's rows at once — CrateDB executes the bulk far cheaper than N individual
    /// statements. Positional parameters (<c>$1, $2, …</c>) bind each set in order.
    /// </summary>
    Task ExecuteBulkAsync(string tenantId, string parameterizedSql,
        IReadOnlyList<IReadOnlyList<object?>> argumentSets, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="ExecuteBulkAsync(string, string, IReadOnlyList{IReadOnlyList{object}}, CancellationToken)"/>,
    /// with the command timeout of <paramref name="statementClass"/> set on the batch (AB#8002) instead
    /// of the driver's 30 s default. The default implementation ignores the class.
    /// </summary>
    Task ExecuteBulkAsync(string tenantId, string parameterizedSql,
        IReadOnlyList<IReadOnlyList<object?>> argumentSets, CrateStatementClass statementClass,
        CancellationToken cancellationToken) =>
        ExecuteBulkAsync(tenantId, parameterizedSql, argumentSets, cancellationToken);

    /// <summary>
    /// Forces CrateDB to apply pending inserts to the read path immediately for the given archive
    /// table. CrateDB applies inserts asynchronously (~1s default) so callers that need strict
    /// read-after-write consistency must invoke this after a bulk insert. Concept §15: callers opt
    /// in; the repository does NOT call it after every insert because it is expensive under load.
    /// </summary>
    Task RefreshArchiveTableAsync(string tenantId, string qualifiedTable);

    /// <summary>
    /// <see cref="RefreshArchiveTableAsync(string, string)"/> under the time budget of
    /// <paramref name="statementClass"/>; the token cancels the wait for a connection and the
    /// refresh itself.
    /// </summary>
    Task RefreshArchiveTableAsync(string tenantId, string qualifiedTable, CrateStatementClass statementClass,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns per-table storage stats (row count, on-disk size, health) for every entry in
    /// <paramref name="tableNames"/> that exists in the tenant's schema. Tables not found in
    /// <c>sys.shards</c> are omitted from the result — callers handle the "not provisioned yet"
    /// case at the next layer up. The existing tables are refreshed (one <c>REFRESH TABLE</c> for
    /// all) before <c>sys.shards</c> is read again, because <c>num_docs</c> lags behind on a shard
    /// CrateDB has not refreshed — a shard nobody searched for 30 s is not refreshed at all (AB#8735).
    /// </summary>
    Task<IReadOnlyList<Dtos.CrateTableStatsRow>> GetTableStatsAsync(
        string tenantId,
        IReadOnlyList<string> tableNames,
        CancellationToken cancellationToken = default);
}