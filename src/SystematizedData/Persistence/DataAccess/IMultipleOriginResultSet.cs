using System.Collections.Generic;
using MongoDB.Bson;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public interface IMultipleOriginResultSet<TChildEntity> : IDictionary<ObjectId, ResultSet<TChildEntity>>
{
}
