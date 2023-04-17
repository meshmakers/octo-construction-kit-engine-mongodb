using System.Collections.Generic;
using MongoDB.Bson;

// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

// ReSharper disable once ClassNeverInstantiated.Global
internal class QueryMultipleResult<TEntity>
{
    public ObjectId Id { get; set; }
    public long TotalCount { get; set; }
    public IEnumerable<TEntity> Targets { get; set; }
}
