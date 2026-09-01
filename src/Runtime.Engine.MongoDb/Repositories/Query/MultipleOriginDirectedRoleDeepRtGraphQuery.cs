using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;

using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

/// <summary>
///     Deep-graph query over a set of directed follow rules (AB#5003). Each rule walks one
///     association role in one direction; hubs (a permission a policy points at, a role a grant
///     points at) are therefore dead-ends and the closure does not spill into the whole identity
///     graph — the semantic the identity exchange needs.
/// </summary>
/// <remarks>
///     The transitive traversal of each rule is done server-side in a single <c>$graphLookup</c>
///     (so a ChildGroup hierarchy resolves in one round trip, not one per level). The only
///     application-side loop is over the rule set, re-running the rules from the growing node set
///     until it reaches a fixpoint — its length is bounded by how the rules feed each other (a
///     handful), not by the graph depth. The result shape is produced by the shared
///     <see cref="DeepGraphEdgeStages" /> tail, byte-identical to the hierarchical query.
/// </remarks>
internal class MultipleOriginDirectedRoleDeepRtGraphQuery
{
    /// <summary>Fail-fast brake against rules feeding each other without ever converging.</summary>
    private const int MaxFixpointRounds = 64;

    private readonly IMongoDbRepositoryDataSource _mongoDbRepositoryDataSource;
    private readonly bool _includeArchivedEntities;
    private readonly IEnumerable<OctoObjectId> _originRtIds;
    private readonly CkTypeGraph _originCkTypeGraph;
    private readonly IReadOnlyCollection<RtDeepGraphFollowSpec> _followSpecs;

    internal MultipleOriginDirectedRoleDeepRtGraphQuery(IMongoDbRepositoryDataSource mongoDbRepositoryDataSource,
        bool includeArchivedEntities, IEnumerable<OctoObjectId> originRtIds, CkTypeGraph originCkTypeGraph,
        IReadOnlyCollection<RtDeepGraphFollowSpec> followSpecs)
    {
        _mongoDbRepositoryDataSource = mongoDbRepositoryDataSource;
        _includeArchivedEntities = includeArchivedEntities;
        _originRtIds = originRtIds;
        _originCkTypeGraph = originCkTypeGraph;
        _followSpecs = followSpecs;
    }

    internal async Task<ResultSet<RtDeepGraphQueryResult>> ExecuteQuery(IOctoSession session, int? skip = null,
        int? take = null)
    {
        var associations = _mongoDbRepositoryDataSource.RtMongoDbDataSourceAssociations;
        var associationCollection = associations.GetMongoCollection();
        var sessionHandle = ((IOctoSessionInternal)session).SessionHandle;

        // Seed the collected node set with the origins (stored ck-type form), so an origin with no
        // matching edge still yields a row via the tail's self-entry.
        var collectedNodes = new Dictionary<OctoObjectId, string>();
        var originMatch = Builders<RtEntity>.Filter.In(x => x.RtId, _originRtIds);
        if (!_includeArchivedEntities)
        {
            originMatch = Builders<RtEntity>.Filter.And(originMatch,
                Builders<RtEntity>.Filter.Ne(x => x.RtState, RtState.Archived));
        }

        var originEntities = await _mongoDbRepositoryDataSource
            .GetRtDatabaseCollection<RtEntity>(_originCkTypeGraph)
            .Aggregate(session)
            .Match(originMatch)
            .ToListAsync();
        foreach (var origin in originEntities)
        {
            if (origin.CkTypeId != null)
            {
                collectedNodes[origin.RtId] = origin.CkTypeId.SemanticVersionedFullName;
            }
        }

        if (collectedNodes.Count == 0)
        {
            return new ResultSet<RtDeepGraphQueryResult>([], 0, null, null);
        }

        // Fixpoint over the rule set: each rule contributes a server-side transitive traversal.
        var rounds = 0;
        var changed = true;
        while (changed)
        {
            if (++rounds > MaxFixpointRounds)
            {
                throw OperationFailedException.DeepGraphFixpointNotReached(MaxFixpointRounds);
            }

            changed = false;
            var frontier = new BsonArray(collectedNodes.Keys.Select(id => (BsonValue)id.ToObjectId()));

            foreach (var spec in _followSpecs)
            {
                var reachedEdges = await TraverseRoleAsync(associationCollection, sessionHandle, spec, frontier);
                foreach (var edge in reachedEdges)
                {
                    if (CollectEnd(collectedNodes, edge, "originRtId", "originCkTypeId"))
                    {
                        changed = true;
                    }

                    if (CollectEnd(collectedNodes, edge, "targetRtId", "targetCkTypeId"))
                    {
                        changed = true;
                    }
                }
            }
        }

        // Build the result shape from the collected node set through the shared tail.
        var uniqueEntities = new BsonArray(collectedNodes.Select(kv => new BsonDocument
        {
            { "rtId", kv.Key.ToObjectId() },
            { "ckTypeId", kv.Value }
        }));

        var tailStages = new List<IPipelineStageDefinition>
        {
            new BsonDocumentPipelineStageDefinition<NoPipelineInput, BsonDocument>(
                new BsonDocument("$documents",
                    new BsonArray { new BsonDocument("uniqueEntities", uniqueEntities) }))
        };
        DeepGraphEdgeStages.AppendEdgeCollectionAndGroup(tailStages, _mongoDbRepositoryDataSource,
            _includeArchivedEntities);

        var pipeline = PipelineDefinition<NoPipelineInput, RtDeepGraphQueryResult>.Create(tailStages);
        var cursor = await associationCollection.Database.AggregateAsync(sessionHandle, pipeline,
            new AggregateOptions { AllowDiskUse = true });
        var allResults = await cursor.ToListAsync();

        // Deterministic ordering keeps exports reproducible; paging is applied in-memory (the graph
        // is fully materialized already).
        var ordered = allResults
            .OrderBy(r => r.Id.RtId.ToString(), StringComparer.Ordinal)
            .ToList();
        var totalCount = ordered.Count;

        IEnumerable<RtDeepGraphQueryResult> page = ordered;
        if (skip.HasValue)
        {
            page = page.Skip(skip.Value);
        }

        if (take.HasValue)
        {
            page = page.Take(take.Value);
        }

        return new ResultSet<RtDeepGraphQueryResult>(page.ToList(), totalCount, null, null);
    }

    private async Task<IReadOnlyList<BsonDocument>> TraverseRoleAsync(
        IMongoCollection<RtAssociation> associationCollection, IClientSessionHandle sessionHandle,
        RtDeepGraphFollowSpec spec, BsonArray frontier)
    {
        // Outbound follows origin->target (seed on origin, chase targetRtId); Inbound the reverse.
        var outbound = spec.Direction == GraphDirections.Outbound;
        var seedField = outbound ? "originRtId" : "targetRtId";
        var connectFromField = outbound ? "targetRtId" : "originRtId";
        var connectToField = outbound ? "originRtId" : "targetRtId";
        var roleValue = spec.RoleId.SemanticVersionedFullName;

        var seedMatch = new BsonDocument
        {
            { "associationRoleId", roleValue },
            { seedField, new BsonDocument("$in", frontier) }
        };
        var restrict = new BsonDocument { { "associationRoleId", roleValue } };
        if (!_includeArchivedEntities)
        {
            seedMatch["rtState"] = new BsonDocument("$ne", (int)RtState.Archived);
            restrict["rtState"] = new BsonDocument("$ne", (int)RtState.Archived);
        }

        var stages = new List<BsonDocument>
        {
            new("$match", seedMatch),
            new("$graphLookup", new BsonDocument
            {
                { "from", associationCollection.CollectionNamespace.CollectionName },
                { "startWith", "$" + connectFromField },
                { "connectFromField", connectFromField },
                { "connectToField", connectToField },
                { "restrictSearchWithMatch", restrict },
                { "as", "_chain" }
            })
        };

        var pipeline = PipelineDefinition<RtAssociation, BsonDocument>.Create(stages);
        var cursor = await associationCollection.AggregateAsync(sessionHandle, pipeline,
            new AggregateOptions { AllowDiskUse = true });
        return await cursor.ToListAsync();
    }

    private static bool CollectEnd(IDictionary<OctoObjectId, string> collectedNodes, BsonDocument edge,
        string rtIdField, string ckTypeIdField)
    {
        var added = AddNode(collectedNodes, edge, rtIdField, ckTypeIdField);
        if (edge.TryGetValue("_chain", out var chain) && chain is BsonArray chainEdges)
        {
            foreach (var chainEdge in chainEdges.OfType<BsonDocument>())
            {
                added |= AddNode(collectedNodes, chainEdge, rtIdField, ckTypeIdField);
            }
        }

        return added;
    }

    private static bool AddNode(IDictionary<OctoObjectId, string> collectedNodes, BsonDocument edge,
        string rtIdField, string ckTypeIdField)
    {
        if (!edge.TryGetValue(rtIdField, out var rtIdValue) || !rtIdValue.IsObjectId ||
            !edge.TryGetValue(ckTypeIdField, out var ckTypeIdValue) || !ckTypeIdValue.IsString)
        {
            return false;
        }

        var rtId = new OctoObjectId(rtIdValue.AsObjectId.ToByteArray());
        return collectedNodes.TryAdd(rtId, ckTypeIdValue.AsString);
    }
}
