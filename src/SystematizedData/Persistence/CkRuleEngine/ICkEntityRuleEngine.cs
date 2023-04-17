using System.Collections.Generic;
using System.Threading.Tasks;

namespace Meshmakers.Octo.Backend.Persistence.CkRuleEngine;

public interface ICkEntityRuleEngine
{
    Task<CkEntityRuleEngineResult> ValidateAsync(IReadOnlyList<EntityUpdateInfo> entityUpdateInfos);
}
