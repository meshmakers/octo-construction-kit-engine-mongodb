using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Persistence.Contracts;

/// <summary>
/// Interface for construction kit model management services
/// </summary>
public interface ICkModelRepository
{
    Task<CkModelId> GetModelIdAsync();
    
    Task ImportAsync(IOctoSession systemSession, ITenantCkModelRepository ckModelRepository);
}