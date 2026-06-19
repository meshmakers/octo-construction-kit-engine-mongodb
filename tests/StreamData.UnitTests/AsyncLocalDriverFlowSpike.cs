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
        Assert.SkipUnless(
            IsMongoReachable("mongodb://localhost:27017/?directConnection=true"),
            "Skipping — no MongoDB reachable at localhost:27017. " +
            "This spike pins driver-thread ExecutionContext propagation; " +
            "run again with the local replica set up to actually verify it.");

        string? observedInStarted = null;
        string? observedInSucceeded = null;

        var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017/?directConnection=true");
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        settings.ClusterConfigurator = cb =>
        {
            // We hook ping (not find) because ping is an auth-free command on any deployment —
            // including the user's local replica set where the test runs without credentials.
            // The whole point of the spike is whether AsyncLocal flows through the driver's
            // event pipeline; the specific command name is incidental.
            cb.Subscribe<CommandStartedEvent>(e =>
            {
                if (e.CommandName == "ping")
                {
                    observedInStarted = Probe.Value;
                }
            });
            cb.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.CommandName == "ping")
                {
                    observedInSucceeded = Probe.Value;
                }
            });
        };

        var client = new MongoClient(settings);

        Probe.Value = "set-on-calling-thread";

        // Force a thread-pool hop between the AsyncLocal write and the driver call —
        // this is the realistic shape of a GraphQL resolver where the request comes in on
        // one thread and the Mongo I/O completes on another.
        await Task.Yield();

        await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
            new BsonDocument("ping", 1),
            cancellationToken: TestContext.Current.CancellationToken);

        // Both observations must equal what we set on the calling thread. If either is null
        // the driver dropped ExecutionContext and Plan A (AsyncLocal) is not viable —
        // fall back to Activity-based correlation.
        Assert.Equal("set-on-calling-thread", observedInStarted);
        Assert.Equal("set-on-calling-thread", observedInSucceeded);
    }

    [Fact]
    public async Task AsyncLocal_IsolatesAcrossParallelRequests()
    {
        Assert.SkipUnless(
            IsMongoReachable("mongodb://localhost:27017/?directConnection=true"),
            "Skipping — no MongoDB reachable at localhost:27017.");

        var observedByRequestId = new System.Collections.Concurrent.ConcurrentDictionary<int, string?>();

        var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017/?directConnection=true");
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        settings.ClusterConfigurator = cb =>
        {
            cb.Subscribe<CommandSucceededEvent>(e =>
            {
                if (e.CommandName == "ping")
                {
                    observedByRequestId[e.RequestId] = Probe.Value;
                }
            });
        };

        var client = new MongoClient(settings);
        var db = client.GetDatabase("admin");

        async Task FireWithMarker(string marker)
        {
            Probe.Value = marker;
            await Task.Yield();
            await db.RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        // Fire 10 concurrent "requests" each setting its own marker. AsyncLocal must not bleed
        // between them — each driver callback must see its caller's marker, not a sibling's.
        var tasks = Enumerable.Range(0, 10).Select(i => FireWithMarker($"req-{i}")).ToArray();
        await Task.WhenAll(tasks);

        // Strong assertion: exactly 10 driver callbacks must have fired (one per request), none
        // saw null, and the observed markers form exactly the expected set — not a subset that
        // could be satisfied by 0 observations. Without this we'd pass even if the driver
        // dropped every event.
        Assert.Equal(10, observedByRequestId.Count);
        Assert.DoesNotContain(null, observedByRequestId.Values);

        var seenMarkers = observedByRequestId.Values.Select(v => v!).ToHashSet();
        var expectedMarkers = Enumerable.Range(0, 10).Select(i => $"req-{i}").ToHashSet();
        Assert.Equal(expectedMarkers, seenMarkers);
    }

    private static bool IsMongoReachable(string connectionString)
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(connectionString);
            // 3 s is enough for handshake/ping but short enough that a missing Mongo skips fast
            // in CI. directConnection=true is set on the URI itself to bypass replica-set
            // topology discovery — that discovery hits container-internal hostnames that the
            // host can't resolve.
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
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
