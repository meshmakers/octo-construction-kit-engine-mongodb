using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Backend.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.Backend.Persistence.DataAccess;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.Persistence;

public interface ITenantContext
{
    string TenantId { get; }

    ITenantRepository Repository { get; }
    ICkCache CkCache { get; }

    Task ExportRtModelAsync(IOctoSession session, OctoObjectId queryId, string filePath,
        CancellationToken? cancellationToken);

    Task ImportRtModelAsync(IOctoSession session, string filePath,
        CancellationToken? cancellationToken);

    Task ImportRtModelAsTextAsync(IOctoSession session, string jsonText);
}
