using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public interface IMultipleOriginResultSet<TChildEntity> : IDictionary<OctoObjectId, IResultSet<TChildEntity>>
{
}
