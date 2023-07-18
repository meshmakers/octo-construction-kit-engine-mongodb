using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

namespace Meshmakers.Octo.Common.Shared.Jobs;

public interface IImportModelJob
{
    /// <summary>
    ///     Imports a CK model
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="key">The key definition in redis</param>
    /// <param name="scopeId">The scope id</param>
    /// <param name="cancellationToken">An cancellation token to abort the job</param>
    /// <returns></returns>
    Task ImportCkAsync(string tenantId, string key, ScopeIdsDto scopeId,
        IBotCancellationToken? cancellationToken);

    /// <summary>
    ///     Imports a runtime model
    /// </summary>
    /// <param name="tenantId">The corresponding tenant</param>
    /// <param name="key">The key definition in redis</param>
    /// <param name="cancellationToken">An cancellation token to abort the job</param>
    /// <returns></returns>
    Task ImportRtAsync(string tenantId, string key, IBotCancellationToken? cancellationToken);
}