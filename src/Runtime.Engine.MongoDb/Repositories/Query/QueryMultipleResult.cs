using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

// ReSharper disable once ClassNeverInstantiated.Global
internal class QueryMultipleResult<TEntity>
{
    public OctoObjectId Id { get; set; }
    public long TotalCount { get; set; }
    public IEnumerable<TEntity> Targets { get; set; } = null!;

    public IEnumerable<GroupingResult>? Grouping { get; set; }
}