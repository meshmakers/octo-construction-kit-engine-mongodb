using System.Collections.Generic;
using System.Threading.Tasks;
using Meshmakers.Octo.Backend.Persistence.DataAccess;

namespace Meshmakers.Octo.Backend.Persistence.CkRuleEngine;

internal interface ICkGraphRuleEngine
{
    Task<GraphRuleEngineResult> ValidateAsync(IOctoSession session,
        IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList);

    Task<GraphRuleEngineResult> ValidateAsync(IOctoSession session,
        IReadOnlyList<EntityUpdateInfo> entityUpdateInfoList,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList);

    Task<GraphRuleEngineResult> ValidateAsync(IOctoSession session,
        IReadOnlyList<AssociationUpdateInfo> associationUpdateInfoList);
}
