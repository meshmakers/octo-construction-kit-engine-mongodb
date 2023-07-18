using System.Threading.Tasks;

namespace Meshmakers.Octo.Common.Shared.Jobs;

public interface IServiceHookJob
{
    Task Run(string dataSource, IBotCancellationToken? cancellationToken);
}