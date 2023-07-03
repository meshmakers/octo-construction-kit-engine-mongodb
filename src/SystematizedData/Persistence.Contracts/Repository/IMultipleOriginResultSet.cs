using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public interface IMultipleOriginResultSet<TChildEntity> : IDictionary<OctoObjectId, IResultSet<TChildEntity>>
{
}
