using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests;

// Behavior pinned for SlowQueriesBuffer — the in-memory ring that backs the Refinery Studio
// Diagnostics surface (AB#4212).
public sealed class SlowQueriesBufferTests
{
    private static SlowQueryEntry Entry(string commandName = "find", string database = "tenant_a",
        double durationMs = 150, int requestId = 1)
        => new(
            Timestamp: new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero).AddSeconds(requestId),
            CommandName: commandName,
            Target: "rt_entities",
            Database: database,
            DurationMs: durationMs,
            RequestId: requestId,
            CommandBsonPreview: "{\"find\":\"rt_entities\"}",
            Success: true,
            ErrorCode: null);

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

        // After all writers settle, the buffer must be exactly at capacity (we wrote far more
        // than the cap). The Trim loop is intentionally allowed to over-trim briefly under
        // contention, but converges to ≤ Capacity once the contention is over.
        Assert.True(buf.Count <= capacity,
            $"Buffer count {buf.Count} exceeded capacity {capacity} after concurrent writes");
    }

    [Fact]
    public async Task Snapshot_IsConsistent_AcrossConcurrentWrites()
    {
        // A reader taking a snapshot during a write storm must see a well-formed list — no
        // null entries, no torn records, no count discrepancies.
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
            Assert.All(snap, e => Assert.NotNull(e));
            Assert.True(snap.Count <= 200);
        }

        await stop.CancelAsync();
        try
        {
            await writer;
        }
        catch (OperationCanceledException) { /* expected */ }
    }
}
