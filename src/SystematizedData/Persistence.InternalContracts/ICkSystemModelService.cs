using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Persistence.InternalContracts;

public interface ICkSystemModelService
{
    Task ImportAsync(IOctoSession systemSession, ITenantCkModelRepository ckModelRepository);
}