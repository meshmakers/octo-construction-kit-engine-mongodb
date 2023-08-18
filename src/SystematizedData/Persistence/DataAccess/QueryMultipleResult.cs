using System.Collections.Generic;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using MongoDB.Bson;

// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

// ReSharper disable once ClassNeverInstantiated.Global
internal class QueryMultipleResult<TEntity>
{
    public OctoObjectId Id { get; set; }
    public long TotalCount { get; set; }
    public IEnumerable<TEntity> Targets { get; set; } = null!;
}
