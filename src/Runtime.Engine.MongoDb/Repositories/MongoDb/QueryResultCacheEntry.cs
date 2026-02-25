using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

internal class QueryResultCacheEntry
{
    public string Id { get; set; } = null!;

    public List<OctoObjectId> EntityIds { get; set; } = [];

    public long TotalCount { get; set; }

    public DateTime CreatedAt { get; set; }
}
