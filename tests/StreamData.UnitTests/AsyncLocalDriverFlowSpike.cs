using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests;

// Spike for AB#4210: does AsyncLocal<T> set on the calling thread reach the MongoDB driver's
// command event callbacks?
//
// The Performance Advisor Stage 2 design hinges on bridging from driver callbacks back to the
// originating HTTP request via AsyncLocal. ExecutionContext usually flows across thread-pool
// hops, but if the driver calls ExecutionContext.SuppressFlow() before raising events the
// AsyncLocal would read as default. This test pins the answer empirically before we build on
// the assumption.
//
// Connects to localhost:27017 (the dev Mongo replica set). Skipped silently if unreachable so
// the unit-test suite still passes in CI / minimal environments.
public sealed class AsyncLocalDriverFlowSpike
{
    private static readonly AsyncLocal<string?> Probe = new();

    [Fact]
    public async Task AsyncLocal_FlowsIntoCommandStartedAndSucceededCallbacks()
    {
        if (!IsMongoReachable("mongodb://localhost:27017"))
        {
            return; // soft-skip when no Mongo is up
        }

        string? observedInStarted = null;
        string? observedInSucceeded = null;

        var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017");
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        settings.ClusterConfigurator = cb =>
        {
            cb.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "find")
                {
                    observedInStarted = Probe.Value;
                }
            });
            cb.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.CommandName == "find")
                {
                    observedInSucceeded = Probe.Value;
                }
            });
        };

        var client = new MongoClient(settings);
        var collection = client.GetDatabase("admin").GetCollection<BsonDocument>("system.version");

        Probe.Value = "set-on-calling-thread";

        // Force a thread-pool hop between the AsyncLocal write and the driver call —
        // this is the realistic shape of a GraphQL resolver where the request comes in on
        // one thread and the Mongo I/O completes on another.
        await Task.Yield();

        await collection.Find(FilterDefinition<BsonDocument>.Empty).Limit(1)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        // Both observations must equal what we set on the calling thread. If either is null
        // the driver dropped ExecutionContext and Plan A (AsyncLocal) is not viable —
        // fall back to Activity-based correlation.
        Assert.Equal("set-on-calling-thread", observedInStarted);
        Assert.Equal("set-on-calling-thread", observedInSucceeded);
    }

    [Fact]
    public async Task AsyncLocal_IsolatesAcrossParallelRequests()
    {
        if (!IsMongoReachable("mongodb://localhost:27017"))
        {
            return;
        }

        var observedByRequestId = new System.Collections.Concurrent.ConcurrentDictionary<int, string?>();

        var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017");
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        settings.ClusterConfigurator = cb =>
        {
            cb.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.CommandName == "find")
                {
                    observedByRequestId[e.RequestId] = Probe.Value;
                }
            });
        };

        var client = new MongoClient(settings);
        var collection = client.GetDatabase("admin").GetCollection<BsonDocument>("system.version");

        async Task FireWithMarker(string marker)
        {
            Probe.Value = marker;
            await Task.Yield();
            await collection.Find(FilterDefinition<BsonDocument>.Empty).Limit(1)
                .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        }

        // Fire 10 concurrent "requests" each setting its own marker. AsyncLocal must not bleed
        // between them — each driver callback must see its caller's marker, not a sibling's.
        var tasks = Enumerable.Range(0, 10).Select(i => FireWithMarker($"req-{i}")).ToArray();
        await Task.WhenAll(tasks);

        // All 10 observations must be in the set we set. None can be null, none can be unrelated.
        var seenMarkers = observedByRequestId.Values.Where(v => v != null).Select(v => v!).Distinct().ToHashSet();
        var expectedMarkers = Enumerable.Range(0, 10).Select(i => $"req-{i}").ToHashSet();
        Assert.Subset(expectedMarkers, seenMarkers);
        Assert.DoesNotContain(null, observedByRequestId.Values);
    }

    private static bool IsMongoReachable(string connectionString)
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(connectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(1);
            var client = new MongoClient(settings);
            client.GetDatabase("admin").RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
