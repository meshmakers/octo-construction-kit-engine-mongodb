using System.Collections.Generic;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.Persistence.DataAccess;

public interface IMultipleOriginResultSet<TChildEntity> : IDictionary<ObjectId, ResultSet<TChildEntity>>
{
}
