using System.Threading.Tasks;

namespace Meshmakers.Octo.Common.Shared.Jobs;

public interface IExportModelJob
{
    /// <summary>
    ///     Exports a runtime model
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="queryId">Id of query, whose data is exported</param>
    /// <param name="cancellationToken">An cancellation token to abort the job</param>
    /// <returns>The key the result file is stored.</returns>
    Task<string> ExportRtAsync(string tenantId, string queryId,
        IBotCancellationToken? cancellationToken);
}