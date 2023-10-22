using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public interface IMultipleOriginResultSet<TChildEntity> : IDictionary<OctoObjectId, IResultSet<TChildEntity>>
{
}
