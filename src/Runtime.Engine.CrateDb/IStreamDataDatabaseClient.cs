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
    /// Get data from the stream data database.
    /// </summary>
    Task<List<DataPointDto>> GetDataAsync(string tenantId, string query);

    /// <summary>
    /// Executes a COUNT query and returns the total number of matching rows.
    /// </summary>
    Task<long> GetCountAsync(string tenantId, string countQuery);

    /// <summary>
    /// Forces CrateDB to apply pending inserts to the read path immediately for the given archive
    /// table. CrateDB applies inserts asynchronously (~1s default) so callers that need strict
    /// read-after-write consistency must invoke this after a bulk insert. Concept §15: callers opt
    /// in; the repository does NOT call it after every insert because it is expensive under load.
    /// </summary>
    Task RefreshArchiveTableAsync(string tenantId, string qualifiedTable);
}