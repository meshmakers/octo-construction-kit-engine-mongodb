using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess.InsertModifiers;

public interface IAutoIncrementModifier
{
    Task RunAutoIncrementAsync(IOctoSession session, CkId<CkTypeId> ckTypeId, IEnumerable<RtEntity> rtEntities);
}