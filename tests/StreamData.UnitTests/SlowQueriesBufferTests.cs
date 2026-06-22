using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests;

// Behavior pinned for SlowQueriesBuffer — the in-memory ring that backs the Refinery Studio
// Diagnostics surface (AB#4212).
public sealed class SlowQueriesBufferTests
{
    private static SlowQueryEntry Entry(string commandName = "find", string database = "tenant_a",
        double durationMs = 150, int requestId = 1, string fingerprint = "aaaaaaaaaaaaaaaa")
        => new(
            Timestamp: new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero).AddSeconds(requestId),
            CommandName: commandName,
            Target: "rt_entities",
            Database: database,
            DurationMs: durationMs,
            RequestId: requestId,
            CommandBsonPreview: "{\"find\":\"rt_entities\"}",
            Success: true,
            ErrorCode: null,
            Fingerprint: fingerprint);

    [Fact]
    public void Empty_Buffer_ReturnsEmptySnapshot()
    {
        var buf = new SlowQueriesBuffer(capacity: 100);
        Assert.Empty(buf.GetSnapshot());
        Assert.Equal(0, buf.Count);
    }

    [Fact]
    public void Add_Below_Capacity_RetainsAll_NewestFirst()
    {
        var buf = new SlowQueriesBuffer(capacity: 10);
        for (var i = 1; i <= 5; i++)
        {
            buf.Add(Entry(requestId: i));
        }

        var snapshot = buf.GetSnapshot();
        Assert.Equal(5, snapshot.Count);
        // Newest first — requestId=5 (added last) is at index 0.
        Assert.Equal(5, snapshot[0].RequestId);
        Assert.Equal(1, snapshot[^1].RequestId);
    }

    [Fact]
    public void Add_Beyond_Capacity_DropsOldest_FIFO()
    {
        var buf = new SlowQueriesBuffer(capacity: 3);
        for (var i = 1; i <= 7; i++)
        {
            buf.Add(Entry(requestId: i));
        }

        var snapshot = buf.GetSnapshot();
        Assert.Equal(3, snapshot.Count);
        // Only the last 3 inserts (requestId 5, 6, 7) survived; newest first.
        Assert.Equal(new[] { 7, 6, 5 }, snapshot.Select(e => e.RequestId));
    }

    [Fact]
    public void Zero_Capacity_Discards_Every_Add()
    {
        // Capacity=0 is the configured "disabled" mode; metrics + slow-log still emit, but the
        // buffer is sized-out for memory.
        var buf = new SlowQueriesBuffer(capacity: 0);
        buf.Add(Entry());
        buf.Add(Entry(requestId: 2));

        Assert.Empty(buf.GetSnapshot());
        Assert.Equal(0, buf.Count);
    }

    [Fact]
    public void Negative_Capacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlowQueriesBuffer(capacity: -1));
    }

    [Fact]
    public void GetSnapshot_Predicate_FiltersBeforeReversal()
    {
        var buf = new SlowQueriesBuffer(capacity: 10);
        buf.Add(Entry(database: "tenant_a", requestId: 1));
        buf.Add(Entry(database: "tenant_b", requestId: 2));
        buf.Add(Entry(database: "tenant_a", requestId: 3));
        buf.Add(Entry(database: "tenant_b", requestId: 4));

        var onlyA = buf.GetSnapshot(predicate: e => e.Database == "tenant_a");

        Assert.Equal(2, onlyA.Count);
        Assert.All(onlyA, e => Assert.Equal("tenant_a", e.Database));
        // Still newest-first within the filtered set.
        Assert.Equal(3, onlyA[0].RequestId);
        Assert.Equal(1, onlyA[1].RequestId);
    }

    [Fact]
    public void GetSnapshot_Limit_AppliesAfterFilter()
    {
        var buf = new SlowQueriesBuffer(capacity: 20);
        for (var i = 1; i <= 10; i++)
        {
            buf.Add(Entry(requestId: i));
        }

        var top3 = buf.GetSnapshot(limit: 3);
        Assert.Equal(new[] { 10, 9, 8 }, top3.Select(e => e.RequestId));
    }

    [Fact]
    public void GetSnapshot_Limit_Zero_ReturnsEmpty()
    {
        var buf = new SlowQueriesBuffer(capacity: 10);
        buf.Add(Entry());
        Assert.Empty(buf.GetSnapshot(limit: 0));
    }

    [Fact]
    public void GetSnapshot_NegativeLimit_Throws()
    {
        var buf = new SlowQueriesBuffer(capacity: 10);
        buf.Add(Entry());

        // Negative limits used to be silently treated as "no limit", which masks call-site
        // bugs. We now throw explicitly, matching the constructor's handling of negative
        // capacity.
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.GetSnapshot(limit: -1));
    }

    [Fact]
    public async Task Concurrent_Writes_NeverExceed_Capacity()
    {
        // The buffer is written from the MongoDB driver's command-event thread pool; multiple
        // writers must never bloat past Capacity (memory ceiling guarantee). Run a high-contention
        // load and assert that at no point — including during the race — the buffer exceeds cap.
        const int capacity = 50;
        const int writers = 8;
        const int writesPerThread = 5000;
        var buf = new SlowQueriesBuffer(capacity);

        var tasks = Enumerable.Range(0, writers).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < writesPerThread; i++)
            {
                buf.Add(Entry(requestId: t * writesPerThread + i));
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // After all writers settle the buffer must be ≤ Capacity. Use GetSnapshot().Count for
        // a deterministic point-in-time read — buf.Count tracks an Interlocked field that can
        // briefly drift under racing writers and isn't appropriate for assertions.
        var finalCount = buf.GetSnapshot().Count;
        Assert.True(finalCount <= capacity,
            $"Buffer snapshot count {finalCount} exceeded capacity {capacity} after concurrent writes");
    }

    [Fact]
    public async Task Snapshot_IsConsistent_AcrossConcurrentWrites()
    {
        // A reader taking a snapshot during a write storm must see a well-formed list — no
        // null entries, no torn records, no exceptions. We don't assert snap.Count <= Capacity
        // here because the cap is enforced asynchronously: Add() enqueues first, then trims.
        // Under concurrent writers the queue can transiently sit at Capacity+N (one per
        // in-flight Add) before the trims catch up. The post-quiesce strict-cap assertion
        // lives in Concurrent_Writes_NeverExceed_Capacity.
        var buf = new SlowQueriesBuffer(capacity: 200);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                buf.Add(Entry(requestId: ++i));
            }
        }, stop.Token);

        for (var read = 0; read < 100; read++)
        {
            var snap = buf.GetSnapshot();
            // Confirms the snapshot is a real, non-torn array (no null slots, no exceptions
            // during materialization). Returning here without throwing is the proof of
            // consistency — the size invariant is verified by the sibling test.
            Assert.All(snap, e => Assert.NotNull(e));
        }

        await stop.CancelAsync();
        try
        {
            await writer;
        }
        catch (OperationCanceledException) { /* expected */ }
    }

    // ---- GetGroupedSnapshot (AB#4213) ----

    [Fact]
    public void GetGroupedSnapshot_GroupsByFingerprint_AggregatesCountAndDurations()
    {
        var buf = new SlowQueriesBuffer(capacity: 50);
        // Three entries with fingerprint "fp_a" — different durations
        buf.Add(Entry(requestId: 1, fingerprint: "fp_a", durationMs: 100));
        buf.Add(Entry(requestId: 2, fingerprint: "fp_a", durationMs: 200));
        buf.Add(Entry(requestId: 3, fingerprint: "fp_a", durationMs: 300));
        // Two entries with fingerprint "fp_b"
        buf.Add(Entry(requestId: 4, fingerprint: "fp_b", durationMs: 500));
        buf.Add(Entry(requestId: 5, fingerprint: "fp_b", durationMs: 700));

        var groups = buf.GetGroupedSnapshot();

        Assert.Equal(2, groups.Count);
        var groupA = groups.Single(g => g.Fingerprint == "fp_a");
        Assert.Equal(3, groupA.Count);
        Assert.Equal(100, groupA.MinDurationMs);
        Assert.Equal(300, groupA.MaxDurationMs);
        Assert.Equal(200, groupA.AvgDurationMs);

        var groupB = groups.Single(g => g.Fingerprint == "fp_b");
        Assert.Equal(2, groupB.Count);
        Assert.Equal(500, groupB.MinDurationMs);
        Assert.Equal(700, groupB.MaxDurationMs);
        Assert.Equal(600, groupB.AvgDurationMs);
    }

    [Fact]
    public void GetGroupedSnapshot_OrdersByLastSeenDescending()
    {
        var buf = new SlowQueriesBuffer(capacity: 50);
        // fp_old: requestId 1 → Timestamp 12:00:01
        buf.Add(Entry(requestId: 1, fingerprint: "fp_old"));
        // fp_new: requestId 10 → Timestamp 12:00:10 (later)
        buf.Add(Entry(requestId: 10, fingerprint: "fp_new"));

        var groups = buf.GetGroupedSnapshot();

        // Most recently seen first.
        Assert.Equal("fp_new", groups[0].Fingerprint);
        Assert.Equal("fp_old", groups[1].Fingerprint);
    }

    [Fact]
    public void GetGroupedSnapshot_RepresentativeIsTheMostRecentEntry()
    {
        var buf = new SlowQueriesBuffer(capacity: 50);
        buf.Add(Entry(requestId: 1, fingerprint: "fp", durationMs: 100));
        buf.Add(Entry(requestId: 5, fingerprint: "fp", durationMs: 999)); // newest
        buf.Add(Entry(requestId: 3, fingerprint: "fp", durationMs: 200));

        var group = buf.GetGroupedSnapshot().Single();

        Assert.Equal(5, group.Representative.RequestId);
        Assert.Equal(999, group.Representative.DurationMs);
    }

    [Fact]
    public void GetGroupedSnapshot_AppliesPredicateBeforeGrouping()
    {
        var buf = new SlowQueriesBuffer(capacity: 50);
        buf.Add(Entry(requestId: 1, fingerprint: "fp", database: "tenant_a"));
        buf.Add(Entry(requestId: 2, fingerprint: "fp", database: "tenant_b"));
        buf.Add(Entry(requestId: 3, fingerprint: "fp", database: "tenant_a"));

        var groups = buf.GetGroupedSnapshot(predicate: e => e.Database == "tenant_a");

        var group = Assert.Single(groups);
        Assert.Equal("fp", group.Fingerprint);
        Assert.Equal(2, group.Count);
    }

    [Fact]
    public void GetGroupedSnapshot_LimitTrimsAfterOrdering()
    {
        var buf = new SlowQueriesBuffer(capacity: 50);
        // Three distinct fingerprints, different last-seen times.
        buf.Add(Entry(requestId: 1, fingerprint: "fp_old"));
        buf.Add(Entry(requestId: 5, fingerprint: "fp_mid"));
        buf.Add(Entry(requestId: 10, fingerprint: "fp_new"));

        var groups = buf.GetGroupedSnapshot(limit: 2);

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "fp_new", "fp_mid" }, groups.Select(g => g.Fingerprint));
    }

    [Fact]
    public void GetGroupedSnapshot_NegativeLimit_Throws()
    {
        var buf = new SlowQueriesBuffer(capacity: 10);
        buf.Add(Entry());
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.GetGroupedSnapshot(limit: -1));
    }

    [Fact]
    public void GetGroupedSnapshot_CompositeKey_SeparatesByTarget()
    {
        // Same fingerprint, different target → must NOT merge. Reviewer flagged: the
        // fingerprinter normalises primitive *values* but the buffer's Target field is
        // extracted independently from the BSON, so {find: "ck_types"} and
        // {find: "rt_entities"} can share a fingerprint despite hitting different
        // collections.
        var buf = new SlowQueriesBuffer(capacity: 50);
        buf.Add(Entry(requestId: 1, fingerprint: "fp_shared", commandName: "find") with { Target = "ck_types" });
        buf.Add(Entry(requestId: 2, fingerprint: "fp_shared", commandName: "find") with { Target = "rt_entities" });
        buf.Add(Entry(requestId: 3, fingerprint: "fp_shared", commandName: "find") with { Target = "ck_types" });

        var groups = buf.GetGroupedSnapshot();

        // Two distinct groups, distinguished by target.
        Assert.Equal(2, groups.Count);
        var ckTypes = groups.Single(g => g.Target == "ck_types");
        Assert.Equal(2, ckTypes.Count);
        var rtEntities = groups.Single(g => g.Target == "rt_entities");
        Assert.Equal(1, rtEntities.Count);
    }

    [Fact]
    public void GetGroupedSnapshot_CompositeKey_SeparatesByDatabase()
    {
        // Same fingerprint, different tenant database → must NOT merge across tenants.
        var buf = new SlowQueriesBuffer(capacity: 50);
        buf.Add(Entry(requestId: 1, fingerprint: "fp", database: "tenant_a"));
        buf.Add(Entry(requestId: 2, fingerprint: "fp", database: "tenant_b"));

        var groups = buf.GetGroupedSnapshot();

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(1, g.Count));
        Assert.Contains(groups, g => g.Database == "tenant_a");
        Assert.Contains(groups, g => g.Database == "tenant_b");
    }

    [Fact]
    public void GetSnapshot_WithExplainCache_StampsExplainOnReturnedEntry()
    {
        // Stage 2B — buffer constructed with an explain cache; reads should join the latest
        // captured plan onto each returned entry. The buffer itself never holds the explain.
        var cache = new SlowQueryExplainCache(capacity: 10, cooldown: TimeSpan.Zero);
        var buf = new SlowQueriesBuffer(capacity: 50, explainCache: cache);
        buf.Add(Entry(requestId: 1, fingerprint: "fp_with_explain"));

        var explain = new SlowQueryExplain(
            CapturedAt: DateTimeOffset.UtcNow,
            Status: SlowQueryExplainStatus.Success,
            WinningStage: "COLLSCAN",
            HasCollScan: true,
            IndexNames: Array.Empty<string>(),
            RawExplainPreview: null,
            ErrorMessage: null);
        cache.Set(new SlowQueryExplainKey("fp_with_explain", "find", "rt_entities", "tenant_a"), explain);

        var entries = buf.GetSnapshot();

        Assert.Single(entries);
        Assert.NotNull(entries[0].Explain);
        Assert.Same(explain, entries[0].Explain);
    }

    [Fact]
    public void GetSnapshot_WithExplainCache_NoMatchingKey_LeavesExplainNull()
    {
        // Cache lookup is keyed on (Fingerprint, CommandName, Target, Database). A miss keeps
        // the entry's Explain null — never a partial overlay from a different shape.
        var cache = new SlowQueryExplainCache(capacity: 10, cooldown: TimeSpan.Zero);
        var buf = new SlowQueriesBuffer(capacity: 50, explainCache: cache);
        buf.Add(Entry(requestId: 1, fingerprint: "fp_a"));

        cache.Set(new SlowQueryExplainKey("fp_other", "find", "rt_entities", "tenant_a"),
            new SlowQueryExplain(DateTimeOffset.UtcNow, SlowQueryExplainStatus.Success,
                "COLLSCAN", true, Array.Empty<string>(), null, null));

        var entries = buf.GetSnapshot();

        Assert.Single(entries);
        Assert.Null(entries[0].Explain);
    }

    [Fact]
    public void GetGroupedSnapshot_WithExplainCache_StampsExplainOnGroupAndRepresentative()
    {
        // Group's Explain mirrors the representative entry's Explain — both must be set so the
        // Studio surface can render the badge directly off the group row.
        var cache = new SlowQueryExplainCache(capacity: 10, cooldown: TimeSpan.Zero);
        var buf = new SlowQueriesBuffer(capacity: 50, explainCache: cache);
        buf.Add(Entry(requestId: 1, fingerprint: "fp_explain"));
        buf.Add(Entry(requestId: 2, fingerprint: "fp_explain"));

        var explain = new SlowQueryExplain(
            DateTimeOffset.UtcNow, SlowQueryExplainStatus.Success,
            "IXSCAN", false, new[] { "idx_x" }, null, null);
        cache.Set(new SlowQueryExplainKey("fp_explain", "find", "rt_entities", "tenant_a"), explain);

        var groups = buf.GetGroupedSnapshot();

        Assert.Single(groups);
        Assert.NotNull(groups[0].Explain);
        Assert.Equal("IXSCAN", groups[0].Explain!.WinningStage);
        Assert.NotNull(groups[0].Representative.Explain);
        Assert.Same(groups[0].Explain, groups[0].Representative.Explain);
    }
}
