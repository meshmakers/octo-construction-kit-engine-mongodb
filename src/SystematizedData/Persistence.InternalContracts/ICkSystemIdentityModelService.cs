using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Persistence.InternalContracts;

public interface ICkSystemIdentityModelService
{
    Task ImportAsync(IOctoSession systemSession, ITenantCkModelRepository ckModelRepository);
}