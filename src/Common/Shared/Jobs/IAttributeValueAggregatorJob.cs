using System.Threading.Tasks;

namespace Meshmakers.Octo.Common.Shared.Jobs;

public interface IAttributeValueAggregatorJob
{
    /// <summary>
    ///     Aggregates all aggregatable attributes
    /// </summary>
    /// <param name="tenantId">The corresponding data source</param>
    /// <param name="cancellationToken">An cancellation token to abort the job</param>
    /// <returns></returns>
    Task Run(string tenantId, IBotCancellationToken? cancellationToken);
}