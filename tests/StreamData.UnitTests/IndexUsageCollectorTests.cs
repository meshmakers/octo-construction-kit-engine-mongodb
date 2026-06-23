using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

// Behavior pinned for the pure projection step of IndexUsageCollector. The actual aggregation
// against MongoDB requires a testcontainer (validated post-deploy); BuildEntries is the
// shape-sensitive transformation that absorbs whatever shape `$indexStats` returns. Worth
// pinning explicitly so a future Mongo driver upgrade that changes the BSON layout fails
// loudly here.
public class IndexUsageCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);

    private static BsonDocument HostStats(string name, long ops, DateTime since, BsonDocument? keySpec = null) =>
        new()
        {
            { "name", name },
            { "key", keySpec ?? new BsonDocument { { "x", 1 } } },
            { "accesses", new BsonDocument
                {
                    { "ops", ops },
                    { "since", since }
                }
            }
        };

    [Fact]
    public void BuildEntries_EmptyInput_ReturnsEmpty()
    {
        var result = IndexUsageCollector.BuildEntries("rt_entities",
            Array.Empty<BsonDocument>(), minAgeDays: 7, lowUsageOpsThreshold: 10, now: Now).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void BuildEntries_SingleHostSingleIndex_ProjectsAllFields()
    {
        var raw = new[]
        {
            HostStats("attributes.name.value_1", ops: 1234, since: Now.AddDays(-30).UtcDateTime,
                keySpec: new BsonDocument { { "attributes.name.value", 1 } })
        };

        var result = IndexUsageCollector.BuildEntries("rt_entities", raw,
            minAgeDays: 7, lowUsageOpsThreshold: 10, now: Now).ToList();

        var entry = Assert.Single(result);
        Assert.Equal("rt_entities", entry.CollectionName);
        Assert.Equal("attributes.name.value_1", entry.IndexName);
        Assert.Contains("attributes.name.value", entry.KeySpec);
        Assert.Equal(1234, entry.OpsCount);
        Assert.Equal(30, entry.AgeDays);
        Assert.False(entry.IsBuiltin);
        Assert.Equal("db.rt_entities.dropIndex(\"attributes.name.value_1\")", entry.DropShellCommand);
        Assert.Equal(IndexUsageStatus.Used, entry.Status);
    }

    [Fact]
    public void BuildEntries_BuiltinIdIndex_FlaggedBuiltin_NoDropCommand()
    {
        // _id_ is always present and always heavily-used. Builtin classification must
        // override the usage figures so the surface renders it read-only.
        var raw = new[]
        {
            HostStats("_id_", ops: 999_999, since: Now.AddDays(-365).UtcDateTime)
        };

        var result = IndexUsageCollector.BuildEntries("rt_entities", raw,
            minAgeDays: 7, lowUsageOpsThreshold: 10, now: Now).ToList();

        var entry = Assert.Single(result);
        Assert.True(entry.IsBuiltin);
        Assert.Null(entry.DropShellCommand);
        Assert.Equal(IndexUsageStatus.Builtin, entry.Status);
    }

    [Fact]
    public void BuildEntries_ReplicaSetMultipleHosts_SumsOpsAndTakesEarliestSince()
    {
        // Primary has the index for 60 days with 800 ops; secondary added it 10 days ago
        // with 50 ops. Worst-case observation window is the primary's 60 days; combined
        // ops = 850 across the replica set.
        var primary = HostStats("idx_1", ops: 800, since: Now.AddDays(-60).UtcDateTime);
        var secondary = HostStats("idx_1", ops: 50, since: Now.AddDays(-10).UtcDateTime);

        var result = IndexUsageCollector.BuildEntries("rt_entities",
            new[] { primary, secondary }, minAgeDays: 7, lowUsageOpsThreshold: 10, now: Now).ToList();

        var entry = Assert.Single(result);
        Assert.Equal(850, entry.OpsCount);
        Assert.Equal(60, entry.AgeDays);
    }

    [Fact]
    public void BuildEntries_MultipleIndexes_OneEntryEach()
    {
        var raw = new[]
        {
            HostStats("a_1", ops: 100, since: Now.AddDays(-30).UtcDateTime),
            HostStats("b_1", ops: 0, since: Now.AddDays(-30).UtcDateTime),
            HostStats("_id_", ops: 5000, since: Now.AddDays(-365).UtcDateTime)
        };

        var result = IndexUsageCollector.BuildEntries("rt_entities", raw,
            minAgeDays: 7, lowUsageOpsThreshold: 10, now: Now).ToList();

        Assert.Equal(3, result.Count);
        Assert.Single(result, e => e.IndexName == "a_1" && e.Status == IndexUsageStatus.Used);
        Assert.Single(result, e => e.IndexName == "b_1" && e.Status == IndexUsageStatus.Unused);
        Assert.Single(result, e => e.IndexName == "_id_" && e.Status == IndexUsageStatus.Builtin);
    }

    [Fact]
    public void BuildEntries_DocumentWithoutName_SkippedSilently()
    {
        // Defensive — if MongoDB's response ever changes shape, we'd rather silently drop a
        // malformed row than throw and hide the rest of the snapshot.
        var raw = new BsonDocument[]
        {
            new() { { "accesses", new BsonDocument { { "ops", 100 }, { "since", Now.UtcDateTime } } } }, // no "name"
            HostStats("valid_1", ops: 50, since: Now.AddDays(-30).UtcDateTime)
        };

        var result = IndexUsageCollector.BuildEntries("rt_entities", raw,
            minAgeDays: 7, lowUsageOpsThreshold: 10, now: Now).ToList();

        Assert.Single(result);
        Assert.Equal("valid_1", result[0].IndexName);
    }

    [Fact]
    public void BuildEntries_DropCommand_EscapesQuoteAndBackslash()
    {
        // Same defensive escape as Stage 2C's createIndex builder. Index names are normally
        // tame, but the helper must hold up if a malformed name ever surfaces.
        var raw = new[]
        {
            HostStats("evil\\name\"with quote", ops: 0, since: Now.AddDays(-30).UtcDateTime)
        };

        var result = IndexUsageCollector.BuildEntries("rt_entities", raw,
            minAgeDays: 7, lowUsageOpsThreshold: 10, now: Now).ToList();

        var entry = Assert.Single(result);
        Assert.Contains("\\\\", entry.DropShellCommand);  // backslash → \\
        Assert.Contains("\\\"", entry.DropShellCommand);  // double-quote → \"
    }

    [Fact]
    public void BuildEntries_ZeroOpsOldEnough_ClassifiedUnused()
    {
        var raw = new[] { HostStats("dead_1", ops: 0, since: Now.AddDays(-90).UtcDateTime) };

        var result = IndexUsageCollector.BuildEntries("rt_entities", raw,
            minAgeDays: 7, lowUsageOpsThreshold: 10, now: Now).ToList();

        Assert.Equal(IndexUsageStatus.Unused, result[0].Status);
    }

    [Fact]
    public void BuildEntries_DegenerateInput_NoSince_FallsBackToNow_NeverFlaggedUnused()
    {
        // If a host returned `accesses` without `since` (driver bug, malformed admin reply),
        // we fall back to age=0 → Used. Better to under-flag than spook the operator with a
        // bogus Unused row.
        var raw = new BsonDocument[]
        {
            new()
            {
                { "name", "weird_1" },
                { "key", new BsonDocument { { "x", 1 } } },
                { "accesses", new BsonDocument { { "ops", 0 } } } // no "since"
            }
        };

        var result = IndexUsageCollector.BuildEntries("rt_entities", raw,
            minAgeDays: 7, lowUsageOpsThreshold: 10, now: Now).ToList();

        Assert.Equal(0, result[0].AgeDays);
        Assert.Equal(IndexUsageStatus.Used, result[0].Status);
    }
}
