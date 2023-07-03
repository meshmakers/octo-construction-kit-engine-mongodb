using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

public interface IImportCkModelCommand
{
    Task ImportTextAsync(IOctoSession session, ITenantCkModelRepository ckModelRepository, string jsonText, ScopeIds scopeId,
        CancellationToken? cancellationToken = null);

    Task ImportAsync(IOctoSession session, ITenantCkModelRepository ckModelRepository, string filePath, ScopeIds scopeId,
        CancellationToken? cancellationToken = null);
}