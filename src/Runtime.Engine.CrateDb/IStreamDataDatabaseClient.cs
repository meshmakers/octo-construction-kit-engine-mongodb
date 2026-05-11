using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Provides data access to a stream data database
/// </summary>
public interface IStreamDataDatabaseClient
{
    /// <summary>
    /// Insert a single datapoint into the stream data database.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="datapoint"></param>
    /// <returns></returns>
    public Task InsertDataAsync(string tenantId, DataPointDto datapoint);

    /// <summary>
    /// Get data from the stream data database.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="query"></param>
    /// <returns></returns>
    Task<List<DataPointDto>> GetDataAsync(string tenantId, string query);

    /// <summary>
    /// Insert multiple datapoints into the stream data database.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="datapoints"></param>
    /// <returns></returns>
    public Task InsertDataAsync(string tenantId, IEnumerable<DataPointDto> datapoints);

    /// <summary>
    /// Executes a COUNT query and returns the total number of matching rows.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="countQuery"></param>
    /// <returns></returns>
    Task<long> GetCountAsync(string tenantId, string countQuery);

    /// <summary>
    /// Forces CrateDB to apply pending inserts to the read path immediately. CrateDB applies
    /// inserts asynchronously (~1s default) so callers that need strict read-after-write
    /// consistency must invoke this after the bulk insert. Concept §15: callers opt in;
    /// repository does NOT call it after every insert because it is expensive under load.
    /// </summary>
    Task RefreshLegacyTableAsync(string tenantId);
}