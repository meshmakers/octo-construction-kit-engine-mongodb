using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

public interface IMultipleOriginResultSet<TChildEntity> : IDictionary<OctoObjectId, IResultSet<TChildEntity>>
{
}