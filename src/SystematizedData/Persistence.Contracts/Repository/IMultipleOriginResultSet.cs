using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public interface IMultipleOriginResultSet<TChildEntity> : IDictionary<OctoObjectId, IResultSet<TChildEntity>>
{
}
