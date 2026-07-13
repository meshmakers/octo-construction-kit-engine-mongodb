using System.Diagnostics.Metrics;
using System.Net;

using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Servers;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests;

// Direct unit tests for MongoCommandObservability — exercising the listener with synthetic
// driver events. No Testcontainers / Docker required. Pins behavior of:
// - Slow-query WARN logging above SlowQueryThresholdMs
// - Full-BSON inclusion gated on SlowQueryFullCommandLogMs
// - Heartbeat command suppression (isMaster, hello, ping, ...)
// - OpenTelemetry histogram + error-counter emission with command_name / database / status tags
public sealed class MongoCommandObservabilityTests : IDisposable
{
    private readonly CapturingLogger _logger = new();
    private readonly StubOptionsMonitor _config = new(new OctoSystemConfiguration());
    private readonly MongoCommandObservability _sut;

    private readonly MeterListener _listener;
    private readonly List<(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)> _histogramObservations = [];
    private readonly List<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> _counterObservations = [];

    public MongoCommandObservabilityTests()
    {
        _sut = new MongoCommandObservability(_logger, _config);

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == MongoCommandObservability.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<double>((inst, value, tags, _) =>
            _histogramObservations.Add((inst.Name, value, tags.ToArray())));
        _listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
            _counterObservations.Add((inst.Name, value, tags.ToArray())));
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void SucceededCommand_BelowThreshold_DoesNotLogWarning()
    {
        _config.Current.SlowQueryThresholdMs = 100;
        var (started, succeeded) = BuildPair("find", "ck_types", "tenant_a", durationMs: 10);

        _sut.OnStarted(started);
        _sut.OnSucceeded(succeeded);

        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void SucceededCommand_AboveThreshold_LogsWarning_WithCommandAndTarget()
    {
        _config.Current.SlowQueryThresholdMs = 50;
        var (started, succeeded) = BuildPair("find", "ck_types", "tenant_a", durationMs: 200);

        _sut.OnStarted(started);
        _sut.OnSucceeded(succeeded);

        var warn = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("find", warn.Message);
        Assert.Contains("ck_types", warn.Message);
        Assert.Contains("tenant_a", warn.Message);
    }

    [Fact]
    public void SucceededCommand_BetweenThresholds_LogsWarning_WithoutBsonBody()
    {
        _config.Current.SlowQueryThresholdMs = 50;
        _config.Current.SlowQueryFullCommandLogMs = 1000;
        var (started, succeeded) = BuildPair("find", "ck_types", "tenant_a", durationMs: 200);

        _sut.OnStarted(started);
        _sut.OnSucceeded(succeeded);

        var warn = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        // The short slow-log identifies the query (find on ck_types) but does NOT
        // include the full BSON command body — that's reserved for the upper threshold.
        Assert.DoesNotContain("command=", warn.Message);
        Assert.DoesNotContain("\"filter\"", warn.Message);
    }

    [Fact]
    public void SucceededCommand_AboveFullCommandThreshold_IncludesBsonPreview()
    {
        _config.Current.SlowQueryThresholdMs = 50;
        _config.Current.SlowQueryFullCommandLogMs = 100;
        var command = new BsonDocument
        {
            { "find", "ck_types" },
            { "filter", new BsonDocument("name", "Asset") }
        };
        var (started, succeeded) = BuildPair("find", command, "tenant_a", durationMs: 500);

        _sut.OnStarted(started);
        _sut.OnSucceeded(succeeded);

        var warn = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("command=", warn.Message);
        Assert.Contains("filter", warn.Message);
    }

    [Fact]
    public void TruncateBson_RespectsByteBudget_NotCharBudget_ForNonAsciiContent()
    {
        // German umlauts encode to 2 UTF-8 bytes each. A naive char-based truncation would
        // under-count the byte cost and overshoot the documented byte budget.
        // 30 "ä" → 30 chars but 60 UTF-8 bytes.
        var command = new BsonDocument("find", new string('ä', 30));

        var result = MongoCommandObservability.TruncateBson(command, maxBytes: 20);

        // The trailing marker is appended verbatim; the JSON prefix alone must respect the budget.
        var prefix = result.Replace("…<truncated>", string.Empty);
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(prefix) <= 20,
            $"Prefix consumed {System.Text.Encoding.UTF8.GetByteCount(prefix)} UTF-8 bytes, budget was 20: {prefix}");
    }

    [Fact]
    public void TruncateBson_DoesNotSplitMultibyteSequence()
    {
        // After truncation, re-encoding to UTF-8 must produce no replacement chars — the prefix
        // ends at a whole-rune boundary.
        var command = new BsonDocument("note", string.Concat(Enumerable.Repeat("äöüß€😀", 20)));

        // Choose a byte budget that would land mid-emoji if truncated naively.
        var result = MongoCommandObservability.TruncateBson(command, maxBytes: 33);

        var prefix = result.Replace("…<truncated>", string.Empty);
        var roundtripped = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(prefix));
        Assert.Equal(prefix, roundtripped);
        Assert.DoesNotContain('�', roundtripped); // U+FFFD = replacement char on invalid input
    }

    [Fact]
    public void TruncateBson_MaxBytesZero_ReturnsEmpty()
    {
        var command = new BsonDocument("find", "ck_types");
        Assert.Equal(string.Empty, MongoCommandObservability.TruncateBson(command, maxBytes: 0));
        Assert.Equal(string.Empty, MongoCommandObservability.TruncateBson(command, maxBytes: -5));
    }

    [Fact]
    public void TruncateBson_ContentFitsBudget_ReturnsUnmodified()
    {
        var command = new BsonDocument("find", "ck");
        var result = MongoCommandObservability.TruncateBson(command, maxBytes: 1024);
        Assert.DoesNotContain("<truncated>", result);
    }

    [Fact]
    public void SucceededCommand_TruncatesBsonPreview_WhenLargerThanLimit()
    {
        _config.Current.SlowQueryThresholdMs = 50;
        _config.Current.SlowQueryFullCommandLogMs = 100;
        _config.Current.SlowQueryCommandPreviewBytes = 64;

        var hugePipeline = new BsonArray(Enumerable.Range(0, 50)
            .Select(i => new BsonDocument("$match", new BsonDocument("k", $"value-{i:D6}"))));
        var command = new BsonDocument { { "aggregate", "rt_entities" }, { "pipeline", hugePipeline } };
        var (started, succeeded) = BuildPair("aggregate", command, "tenant_a", durationMs: 500);

        _sut.OnStarted(started);
        _sut.OnSucceeded(succeeded);

        var warn = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("<truncated>", warn.Message);
    }

    [Theory]
    [InlineData("isMaster")]
    [InlineData("hello")]
    [InlineData("ping")]
    [InlineData("buildInfo")]
    [InlineData("saslStart")]
    public void HeartbeatCommands_AreIgnored_NoLogNoMetric(string commandName)
    {
        _config.Current.SlowQueryThresholdMs = 1;
        var (started, succeeded) = BuildPair(commandName, "admin", "admin", durationMs: 9999);

        _sut.OnStarted(started);
        _sut.OnSucceeded(succeeded);

        Assert.Empty(_logger.Entries);
        Assert.Empty(_histogramObservations);
        Assert.Empty(_counterObservations);
    }

    [Fact]
    public void SucceededCommand_RecordsHistogram_WithCommandNameDatabaseAndStatusTags()
    {
        var (started, succeeded) = BuildPair("aggregate", "rt_entities", "tenant_a", durationMs: 42);

        _sut.OnStarted(started);
        _sut.OnSucceeded(succeeded);

        _listener.RecordObservableInstruments();
        var obs = Assert.Single(_histogramObservations);
        Assert.Equal("octo.mongodb.command.duration", obs.Instrument);
        Assert.Equal(42, obs.Value);
        Assert.Contains(obs.Tags, t => t.Key == "command_name" && (string?)t.Value == "aggregate");
        Assert.Contains(obs.Tags, t => t.Key == "database" && (string?)t.Value == "tenant_a");
        Assert.Contains(obs.Tags, t => t.Key == "status" && (string?)t.Value == "success");
    }

    [Fact]
    public void FailedCommand_RecordsHistogram_CounterAndWarning()
    {
        var (started, failed) = BuildFailedPair("update", "rt_entities", "tenant_a",
            durationMs: 17, errorCode: 112, errorMessage: "WriteConflict");

        _sut.OnStarted(started);
        _sut.OnFailed(failed);

        // Histogram with status=failure
        var hist = Assert.Single(_histogramObservations);
        Assert.Equal("octo.mongodb.command.duration", hist.Instrument);
        Assert.Contains(hist.Tags, t => t.Key == "status" && (string?)t.Value == "failure");

        // Error counter with the error code
        var cnt = Assert.Single(_counterObservations);
        Assert.Equal("octo.mongodb.command.errors", cnt.Instrument);
        Assert.Equal(1, cnt.Value);
        Assert.Contains(cnt.Tags, t => t.Key == "error_code" && (string?)t.Value == "112");

        // Warning log entry
        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void SucceededCommand_WithoutPriorStarted_FallsBackTo_UnknownDatabase()
    {
        // RequestId-map miss (e.g. driver dropped the started event due to backpressure or
        // a crashed observer) — the listener must degrade gracefully and tag database=unknown
        // rather than throw. The driver itself always populates DatabaseNamespace on the event,
        // but our class deliberately reads it from the RequestId map (because the failed-event
        // also needs it and historic API symmetry matters).
        var (_, succeeded) = BuildPair("find", "ck_types", "tenant_a", durationMs: 5, requestId: 999);

        // Intentionally skip OnStarted to simulate the map miss.
        _sut.OnSucceeded(succeeded);

        var obs = Assert.Single(_histogramObservations);
        Assert.Contains(obs.Tags, t => t.Key == "database" && (string?)t.Value == "unknown");
    }

    // ---- SlowQueriesBuffer integration (AB#4212) ----

    [Fact]
    public void SlowCommand_WritesToSlowQueriesBuffer()
    {
        var buffer = new SlowQueriesBuffer(capacity: 100);
        var sut = new MongoCommandObservability(_logger, _config, buffer);
        _config.Current.SlowQueryThresholdMs = 50;

        var (started, succeeded) = BuildPair("aggregate", "rt_entities", "tenant_a", durationMs: 250);
        sut.OnStarted(started);
        sut.OnSucceeded(succeeded);

        var entries = buffer.GetSnapshot();
        var entry = Assert.Single(entries);
        Assert.Equal("aggregate", entry.CommandName);
        Assert.Equal("rt_entities", entry.Target);
        Assert.Equal("tenant_a", entry.Database);
        Assert.Equal(250, entry.DurationMs, precision: 4);
        Assert.True(entry.Success);
        Assert.Null(entry.ErrorCode);
        Assert.Contains("rt_entities", entry.CommandBsonPreview);
    }

    [Fact]
    public void FastCommand_DoesNotWriteToSlowQueriesBuffer()
    {
        var buffer = new SlowQueriesBuffer(capacity: 100);
        var sut = new MongoCommandObservability(_logger, _config, buffer);
        _config.Current.SlowQueryThresholdMs = 100;

        var (started, succeeded) = BuildPair("find", "ck_types", "tenant_a", durationMs: 10);
        sut.OnStarted(started);
        sut.OnSucceeded(succeeded);

        Assert.Empty(buffer.GetSnapshot());
    }

    [Fact]
    public void HeartbeatCommand_DoesNotWriteToBuffer_EvenIfSlow()
    {
        // Heartbeats are filtered at the very top of OnSucceeded/OnFailed before any other
        // path. They must never reach the buffer regardless of duration.
        var buffer = new SlowQueriesBuffer(capacity: 100);
        var sut = new MongoCommandObservability(_logger, _config, buffer);
        _config.Current.SlowQueryThresholdMs = 1;

        var (started, succeeded) = BuildPair("isMaster", "admin", "admin", durationMs: 5000);
        sut.OnStarted(started);
        sut.OnSucceeded(succeeded);

        Assert.Empty(buffer.GetSnapshot());
    }

    [Fact]
    public void FailedSlowCommand_WritesToBuffer_WithErrorCode_AndSuccessFalse()
    {
        var buffer = new SlowQueriesBuffer(capacity: 100);
        var sut = new MongoCommandObservability(_logger, _config, buffer);
        _config.Current.SlowQueryThresholdMs = 50;

        var (started, failed) = BuildFailedPair("update", "rt_entities", "tenant_a",
            durationMs: 200, errorCode: 112, errorMessage: "WriteConflict");
        sut.OnStarted(started);
        sut.OnFailed(failed);

        var entry = Assert.Single(buffer.GetSnapshot());
        Assert.False(entry.Success);
        Assert.Equal("112", entry.ErrorCode);
        Assert.Equal("update", entry.CommandName);
    }

    // ---- RawBsonDocument retention safety (AB#4368) ----
    // For bulk-write commands the driver's CommandStartedEvent.Command is a RawBsonDocument
    // (or a BsonDocument containing RawBsonDocument/RawBsonArray slices) over the connection's
    // send buffer, which the driver returns to its pool right after the started-event handlers
    // finish. These tests dispose the raw source between OnStarted and OnSucceeded — exactly
    // the production sequence that flooded identity-services startup with
    // ObjectDisposedException ERROR spam and dropped the slow entries from the buffer.

    [Fact]
    public void SlowBulkWrite_TopLevelRawCommand_DisposedAfterStarted_StillCapturedWithoutError()
    {
        var buffer = new SlowQueriesBuffer(capacity: 100);
        var sut = new MongoCommandObservability(_logger, _config, buffer);
        _config.Current.SlowQueryThresholdMs = 50;

        var rawCommand = BuildRawInsertCommand();
        var (started, succeeded) = BuildPair("insert", rawCommand, "tenant_a", durationMs: 250);

        sut.OnStarted(started);
        rawCommand.Dispose(); // driver returns the send buffer to its pool after the started event
        sut.OnSucceeded(succeeded);

        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Error);
        var entry = Assert.Single(buffer.GetSnapshot());
        Assert.Contains("rt_entities", entry.CommandBsonPreview);
        Assert.Contains("elided", entry.CommandBsonPreview); // bulk payload replaced, not retained
        Assert.NotEqual(new string('0', SlowQueryFingerprinter.FingerprintLength), entry.Fingerprint);
    }

    [Fact]
    public void SlowBulkWrite_NestedRawSlices_DisposedAfterStarted_StillCapturedWithoutError()
    {
        var buffer = new SlowQueriesBuffer(capacity: 100);
        var sut = new MongoCommandObservability(_logger, _config, buffer);
        _config.Current.SlowQueryThresholdMs = 50;

        // The second production shape: a materialized command document whose payload values
        // are still raw slices over the source buffer.
        var rawSource = BuildRawInsertCommand();
        var command = new BsonDocument
        {
            { "insert", "rt_entities" },
            { "documents", rawSource["documents"] },       // RawBsonArray slice
            { "writeConcern", rawSource["writeConcern"] }  // RawBsonDocument slice
        };
        var (started, succeeded) = BuildPair("insert", command, "tenant_a", durationMs: 250);

        sut.OnStarted(started);
        rawSource.Dispose();
        sut.OnSucceeded(succeeded);

        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Error);
        var entry = Assert.Single(buffer.GetSnapshot());
        Assert.Contains("elided", entry.CommandBsonPreview);
        // Small metadata sub-documents are cloned, not elided.
        Assert.Contains("writeConcern", entry.CommandBsonPreview);
    }

    [Fact]
    public void MaterializeForRetention_PlainDocument_ReturnsSameInstance()
    {
        // No raw content → no copy on the hot path.
        var command = new BsonDocument
        {
            { "find", "ck_types" },
            { "filter", new BsonDocument("name", "Asset") }
        };

        Assert.Same(command, MongoCommandObservability.MaterializeForRetention(command));
    }

    [Fact]
    public void TruncateBson_DisposedRawDocument_ReturnsPlaceholder_InsteadOfThrowing()
    {
        // Defense in depth: even if a disposed raw document slips past the OnStarted snapshot
        // (future driver change), the preview must degrade instead of throwing.
        var raw = BuildRawInsertCommand();
        raw.Dispose();

        var result = MongoCommandObservability.TruncateBson(raw, maxBytes: 2048);

        Assert.Equal("<command unavailable: driver buffer disposed>", result);
    }

    [Fact]
    public void Fingerprint_DisposedRawDocument_ReturnsUnknownShape_InsteadOfThrowing()
    {
        var raw = BuildRawInsertCommand();
        raw.Dispose();

        Assert.Equal(
            new string('0', SlowQueryFingerprinter.FingerprintLength),
            SlowQueryFingerprinter.Fingerprint(raw));
    }

    private static RawBsonDocument BuildRawInsertCommand()
    {
        var command = new BsonDocument
        {
            { "insert", "rt_entities" },
            { "documents", new BsonArray
                {
                    new BsonDocument("attributes", new BsonDocument("name", "a")),
                    new BsonDocument("attributes", new BsonDocument("name", "b"))
                }
            },
            { "writeConcern", new BsonDocument("w", 1) }
        };
        return new RawBsonDocument(command.ToBson());
    }

    [Fact]
    public void NoBufferProvided_DoesNotThrow_AndStillLogs()
    {
        // The buffer is an optional dependency — Mesh-Adapter / bot-services / unit tests can
        // construct the listener without one. Slow-log + metrics must still fire.
        var sutWithoutBuffer = new MongoCommandObservability(_logger, _config);
        _config.Current.SlowQueryThresholdMs = 50;

        var (started, succeeded) = BuildPair("find", "ck_types", "tenant_a", durationMs: 250);
        var ex = Record.Exception(() =>
        {
            sutWithoutBuffer.OnStarted(started);
            sutWithoutBuffer.OnSucceeded(succeeded);
        });

        Assert.Null(ex);
        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    // ---- RequestScope tests (AB#4210) ----

    [Fact]
    public void RequestScope_AccumulatesCommands_Until_Disposed()
    {
        using (MongoCommandObservability.BeginRequestScope(out var stats))
        {
            var (s1, ok1) = BuildPair("find", "ck_types", "tenant_a", durationMs: 12);
            var (s2, ok2) = BuildPair("aggregate", "rt_entities", "tenant_a", durationMs: 47, requestId: 2);
            var (s3, ok3) = BuildPair("find", "ck_types", "tenant_a", durationMs: 3, requestId: 3);

            _sut.OnStarted(s1); _sut.OnSucceeded(ok1);
            _sut.OnStarted(s2); _sut.OnSucceeded(ok2);
            _sut.OnStarted(s3); _sut.OnSucceeded(ok3);

            Assert.Equal(3, stats.CommandCount);
            Assert.Equal(62, stats.TotalMs, precision: 4);
            Assert.Equal(47, stats.SlowestMs, precision: 4);
            Assert.Equal("aggregate", stats.SlowestCommand);
        }

        // After disposal the scope is gone — further commands must not throw and must not
        // touch the now-orphaned stats instance.
        Assert.Null(MongoCommandObservability.GetCurrentScope());
    }

    [Fact]
    public void RequestScope_RecordsFailureDurations_Too()
    {
        using var _ = MongoCommandObservability.BeginRequestScope(out var stats);

        var (started, failed) = BuildFailedPair("update", "rt_entities", "tenant_a",
            durationMs: 25, errorCode: 112, errorMessage: "WriteConflict");
        _sut.OnStarted(started);
        _sut.OnFailed(failed);

        Assert.Equal(1, stats.CommandCount);
        Assert.Equal(25, stats.TotalMs, precision: 4);
        Assert.Equal("update", stats.SlowestCommand);
    }

    [Fact]
    public void RequestScope_IgnoresHeartbeatCommands()
    {
        using var _ = MongoCommandObservability.BeginRequestScope(out var stats);

        var (s1, ok1) = BuildPair("hello", "admin", "admin", durationMs: 8);
        var (s2, ok2) = BuildPair("isMaster", "admin", "admin", durationMs: 5, requestId: 2);
        var (s3, ok3) = BuildPair("ping", "admin", "admin", durationMs: 4, requestId: 3);
        var (s4, ok4) = BuildPair("find", "ck_types", "tenant_a", durationMs: 17, requestId: 4);

        _sut.OnStarted(s1); _sut.OnSucceeded(ok1);
        _sut.OnStarted(s2); _sut.OnSucceeded(ok2);
        _sut.OnStarted(s3); _sut.OnSucceeded(ok3);
        _sut.OnStarted(s4); _sut.OnSucceeded(ok4);

        // Only the find should appear — heartbeats remain excluded from the user-facing surface.
        Assert.Equal(1, stats.CommandCount);
        Assert.Equal(17, stats.TotalMs, precision: 4);
        Assert.Equal("find", stats.SlowestCommand);
    }

    [Fact]
    public void RequestScope_OutOfScope_IsNoOp_NoExceptions()
    {
        // Bare-metal call without an active scope (the Mesh-Adapter / background-job shape).
        // Listener must behave exactly as before: histogram is emitted, no scope side effects,
        // no exceptions.
        Assert.Null(MongoCommandObservability.GetCurrentScope());

        var (started, succeeded) = BuildPair("find", "ck_types", "tenant_a", durationMs: 5);
        var ex = Record.Exception(() =>
        {
            _sut.OnStarted(started);
            _sut.OnSucceeded(succeeded);
        });

        Assert.Null(ex);
        Assert.Single(_histogramObservations);
    }

    [Fact]
    public async Task RequestScope_FlowsThroughAwaitBoundaries()
    {
        using var _ = MongoCommandObservability.BeginRequestScope(out var stats);

        var (s1, ok1) = BuildPair("find", "ck_types", "tenant_a", durationMs: 5);
        _sut.OnStarted(s1);
        _sut.OnSucceeded(ok1);

        // Realistic shape: a resolver awaits something between Mongo calls. The scope must
        // still be the same RequestMongoStats instance after the continuation.
        await Task.Yield();
        await Task.Delay(1, TestContext.Current.CancellationToken);

        var (s2, ok2) = BuildPair("aggregate", "rt_entities", "tenant_a", durationMs: 11, requestId: 2);
        _sut.OnStarted(s2);
        _sut.OnSucceeded(ok2);

        Assert.Equal(2, stats.CommandCount);
        Assert.Equal(16, stats.TotalMs, precision: 4);
    }

    [Fact]
    public async Task RequestScope_IsolatesParallelRequests()
    {
        // Two pseudo-requests running concurrently must not see each other's commands.
        //
        // Deterministic barrier: each branch signals "ready" once it has entered its scope and
        // emitted OnStarted; the test waits for BOTH ready signals before releasing the shared
        // gate. This guarantees both branches are paused inside their respective scopes when the
        // OnSucceeded calls fire — Task.Delay-based timing would let this test pass without
        // ever actually exercising parallel scopes.
        var gate = new TaskCompletionSource();
        var ready1 = new TaskCompletionSource();
        var ready2 = new TaskCompletionSource();

        async Task<RequestMongoStats> RunPseudoRequest(
            string commandName, double durationMs, int requestIdBase, TaskCompletionSource ready)
        {
            using var scope = MongoCommandObservability.BeginRequestScope(out var stats);
            var (s1, ok1) = BuildPair(commandName, "tgt", "tenant_a", durationMs, requestIdBase);
            _sut.OnStarted(s1);
            ready.SetResult();
            await gate.Task;
            _sut.OnSucceeded(ok1);
            return stats;
        }

        var task1 = RunPseudoRequest("find", durationMs: 10, requestIdBase: 100, ready1);
        var task2 = RunPseudoRequest("aggregate", durationMs: 25, requestIdBase: 200, ready2);

        await Task.WhenAll(ready1.Task, ready2.Task).WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        gate.SetResult();

        var statsA = await task1;
        var statsB = await task2;

        // Each scope sees only its own command. If AsyncLocal bled, one would see 2 and the
        // other 0 — or the totals would mix.
        Assert.Equal(1, statsA.CommandCount);
        Assert.Equal(10, statsA.TotalMs, precision: 4);
        Assert.Equal(1, statsB.CommandCount);
        Assert.Equal(25, statsB.TotalMs, precision: 4);
    }

    [Fact]
    public void RequestScope_NestedScopes_RestoreOnDispose()
    {
        using (MongoCommandObservability.BeginRequestScope(out var outer))
        {
            var outerRef = MongoCommandObservability.GetCurrentScope();
            Assert.Same(outer, outerRef);

            using (MongoCommandObservability.BeginRequestScope(out var inner))
            {
                Assert.Same(inner, MongoCommandObservability.GetCurrentScope());
                Assert.NotSame(outer, inner);
            }

            // Inner disposed → outer is restored, not lost.
            Assert.Same(outer, MongoCommandObservability.GetCurrentScope());
        }

        Assert.Null(MongoCommandObservability.GetCurrentScope());
    }

    // ---- Stage 2B explain capture dispatch guards (AB#4216) ----
    // We don't fake an IMongoClient in unit tests (the driver's interface surface is enormous
    // and mocking it brittle). These tests verify the guard layer above the driver call: when
    // do we dispatch, when do we skip, and when do we stamp the cache directly without a
    // round-trip. The actual driver round-trip is validated manually against a real Mongo
    // instance post-deployment — see CLAUDE.md Observability section.

    [Fact]
    public void Explain_FeatureDisabled_DoesNotTouchCache()
    {
        var buffer = new SlowQueriesBuffer(capacity: 100);
        var cache = new SlowQueryExplainCache(capacity: 100, cooldown: TimeSpan.Zero);
        var sut = new MongoCommandObservability(_logger, _config, buffer, cache);
        _config.Current.SlowQueryThresholdMs = 1;
        _config.Current.SlowQueryExplainEnabled = false;

        var (started, succeeded) = BuildPair("find", "ck_types", "tenant_a", durationMs: 250);
        sut.OnStarted(started);
        sut.OnSucceeded(succeeded);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Explain_NoClientWired_DoesNotTouchCache()
    {
        // SetMongoClient never called — listener silently skips dispatch. This is the
        // pre-startup shape (between observability construction and MongoClient creation).
        var buffer = new SlowQueriesBuffer(capacity: 100);
        var cache = new SlowQueryExplainCache(capacity: 100, cooldown: TimeSpan.Zero);
        var sut = new MongoCommandObservability(_logger, _config, buffer, cache);
        _config.Current.SlowQueryThresholdMs = 1;
        _config.Current.SlowQueryExplainEnabled = true;

        var (started, succeeded) = BuildPair("find", "ck_types", "tenant_a", durationMs: 250);
        sut.OnStarted(started);
        sut.OnSucceeded(succeeded);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Explain_FailedCommand_DoesNotDispatch()
    {
        // Failed slow commands write to the buffer but should not trigger an explain probe —
        // there is no winning plan to capture, and re-running a failing command is wasteful.
        var buffer = new SlowQueriesBuffer(capacity: 100);
        var cache = new SlowQueryExplainCache(capacity: 100, cooldown: TimeSpan.Zero);
        var sut = new MongoCommandObservability(_logger, _config, buffer, cache);
        sut.SetMongoClient(new ThrowingMongoClient());  // would throw if dispatch fired
        _config.Current.SlowQueryThresholdMs = 1;
        _config.Current.SlowQueryExplainEnabled = true;

        var (started, failed) = BuildFailedPair("find", "ck_types", "tenant_a",
            durationMs: 250, errorCode: 11600, errorMessage: "InterruptedAtShutdown");
        sut.OnStarted(started);
        sut.OnFailed(failed);

        // Buffer still gets the failed entry — explain just stayed out.
        Assert.Single(buffer.GetSnapshot());
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Explain_UnsupportedCommand_StampsCacheUnsupportedAndSkipsDriverCall()
    {
        // The guard runs synchronously BEFORE the Task.Run dispatch, so even with a throwing
        // client wired the unsupported case must not fire the driver.
        var buffer = new SlowQueriesBuffer(capacity: 100);
        var cache = new SlowQueryExplainCache(capacity: 100, cooldown: TimeSpan.Zero);
        var sut = new MongoCommandObservability(_logger, _config, buffer, cache);
        sut.SetMongoClient(new ThrowingMongoClient());
        _config.Current.SlowQueryThresholdMs = 1;
        _config.Current.SlowQueryExplainEnabled = true;

        // 'insert' is not in IsExplainable's allowed set.
        var (started, succeeded) = BuildPair("insert", "rt_entities", "tenant_a", durationMs: 250);
        sut.OnStarted(started);
        sut.OnSucceeded(succeeded);

        var entry = Assert.Single(buffer.GetSnapshot());
        var stamped = cache.TryGet(new SlowQueryExplainKey(
            entry.Fingerprint, entry.CommandName, entry.Target, entry.Database));
        Assert.NotNull(stamped);
        Assert.Equal(SlowQueryExplainStatus.Unsupported, stamped.Status);
    }

    [Fact]
    public void Explain_NoBuffer_DoesNotDispatch()
    {
        // Without a buffer the listener never computes a fingerprint — so there's no key to
        // dispatch under. Stage 2B is consciously gated on Stage 2A buffer wiring.
        var cache = new SlowQueryExplainCache(capacity: 100, cooldown: TimeSpan.Zero);
        var sut = new MongoCommandObservability(_logger, _config, slowQueries: null, explainCache: cache);
        sut.SetMongoClient(new ThrowingMongoClient());
        _config.Current.SlowQueryThresholdMs = 1;
        _config.Current.SlowQueryExplainEnabled = true;

        var (started, succeeded) = BuildPair("find", "ck_types", "tenant_a", durationMs: 250);
        sut.OnStarted(started);
        sut.OnSucceeded(succeeded);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Explain_CacheCooldownPreventsRedispatch()
    {
        // First fire stamps a cache entry; a second fire with the same shape must observe
        // ShouldCapture=false (cooldown active) and skip dispatch. We seed the cache directly
        // with a recent entry to make the test deterministic — the Unsupported path is the
        // most convenient way to populate the cache without faking a driver round-trip.
        var buffer = new SlowQueriesBuffer(capacity: 100);
        var cache = new SlowQueryExplainCache(capacity: 100, cooldown: TimeSpan.FromMinutes(5));
        var sut = new MongoCommandObservability(_logger, _config, buffer, cache);
        sut.SetMongoClient(new ThrowingMongoClient());
        _config.Current.SlowQueryThresholdMs = 1;
        _config.Current.SlowQueryExplainEnabled = true;

        // Fire once with an unsupported command — stamps cache with Unsupported, dispatch
        // never reaches the client.
        var (s1, ok1) = BuildPair("insert", "rt_entities", "tenant_a", durationMs: 250);
        sut.OnStarted(s1);
        sut.OnSucceeded(ok1);
        var sizeAfterFirst = cache.Count;

        // Fire twice more with the same shape — should still be exactly one cache entry
        // (cooldown active, no re-stamp, no driver call).
        for (var i = 0; i < 2; i++)
        {
            var (s, ok) = BuildPair("insert", "rt_entities", "tenant_a", durationMs: 250, requestId: 100 + i);
            sut.OnStarted(s);
            sut.OnSucceeded(ok);
        }

        Assert.Equal(sizeAfterFirst, cache.Count);
    }

    [Fact]
    public void Observability_DoesNotPropagateExceptions_FromLogger()
    {
        // A logger that throws must not break the driver's event pipeline.
        var throwingLogger = new ThrowingLogger();
        var sut = new MongoCommandObservability(throwingLogger, _config);
        var (started, succeeded) = BuildPair("find", "ck_types", "tenant_a", durationMs: 5);

        sut.OnStarted(started);
        // OnSucceeded does not log when below threshold; force a slow log.
        _config.Current.SlowQueryThresholdMs = 1;
        var (started2, succeeded2) = BuildPair("find", "ck_types", "tenant_a", durationMs: 200);
        sut.OnStarted(started2);

        var ex = Record.Exception(() => sut.OnSucceeded(succeeded2));
        Assert.Null(ex);
    }

    // -- helpers -----------------------------------------------------------

    private static (CommandStartedEvent, CommandSucceededEvent) BuildPair(
        string commandName, string target, string database, double durationMs, int requestId = 1)
    {
        var command = new BsonDocument(commandName, target);
        return BuildPair(commandName, command, database, durationMs, requestId);
    }

    private static (CommandStartedEvent, CommandSucceededEvent) BuildPair(
        string commandName, BsonDocument command, string database, double durationMs, int requestId = 1)
    {
        var dbNs = new DatabaseNamespace(database);
        var connId = FakeConnectionId();
        var started = new CommandStartedEvent(commandName, command, dbNs, operationId: 1, requestId, connId);
        var succeeded = new CommandSucceededEvent(commandName, reply: [], dbNs,
            operationId: 1, requestId, connId, TimeSpan.FromMilliseconds(durationMs));
        return (started, succeeded);
    }

    private static (CommandStartedEvent, CommandFailedEvent) BuildFailedPair(
        string commandName, string target, string database, double durationMs, int errorCode, string errorMessage,
        int requestId = 1)
    {
        var command = new BsonDocument(commandName, target);
        var dbNs = new DatabaseNamespace(database);
        var connId = FakeConnectionId();
        var started = new CommandStartedEvent(commandName, command, dbNs, operationId: 1, requestId, connId);
        var failure = new MongoCommandException(connId, errorMessage, command,
            new BsonDocument { { "ok", 0 }, { "code", errorCode }, { "errmsg", errorMessage } });
        var failed = new CommandFailedEvent(commandName, dbNs, failure, operationId: 1, requestId, connId,
            TimeSpan.FromMilliseconds(durationMs));
        return (started, failed);
    }

    private static ConnectionId FakeConnectionId()
    {
        var clusterId = new ClusterId(1);
        var serverId = new ServerId(clusterId, new DnsEndPoint("localhost", 27017));
        return new ConnectionId(serverId);
    }

    private sealed class CapturingLogger : ILogger<MongoCommandObservability>
    {
        public List<LogEntry> Entries { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
    }

    private sealed class ThrowingLogger : ILogger<MongoCommandObservability>
    {
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("logger boom");
    }

    /// <summary>
    /// Trip-wire IMongoClient: every call throws. Used by the Stage 2B guard tests to assert
    /// that the dispatch path exited BEFORE reaching the driver. If a guard regression let
    /// the dispatch through, GetDatabase would throw and the test would fail loudly.
    /// </summary>
    private sealed class ThrowingMongoClient : IMongoClient
    {
        private static InvalidOperationException Boom() =>
            new("Trip-wire IMongoClient — guard should have prevented this call.");

        public ICluster Cluster => throw Boom();
        public MongoClientSettings Settings => throw Boom();
        public IMongoDatabase GetDatabase(string name, MongoDatabaseSettings? settings = null) => throw Boom();
        public void DropDatabase(string name, CancellationToken cancellationToken = default) => throw Boom();
        public void DropDatabase(IClientSessionHandle session, string name, CancellationToken cancellationToken = default) => throw Boom();
        public Task DropDatabaseAsync(string name, CancellationToken cancellationToken = default) => throw Boom();
        public Task DropDatabaseAsync(IClientSessionHandle session, string name, CancellationToken cancellationToken = default) => throw Boom();
        public IAsyncCursor<string> ListDatabaseNames(CancellationToken cancellationToken = default) => throw Boom();
        public IAsyncCursor<string> ListDatabaseNames(ListDatabaseNamesOptions options, CancellationToken cancellationToken = default) => throw Boom();
        public IAsyncCursor<string> ListDatabaseNames(IClientSessionHandle session, CancellationToken cancellationToken = default) => throw Boom();
        public IAsyncCursor<string> ListDatabaseNames(IClientSessionHandle session, ListDatabaseNamesOptions options, CancellationToken cancellationToken = default) => throw Boom();
        public Task<IAsyncCursor<string>> ListDatabaseNamesAsync(CancellationToken cancellationToken = default) => throw Boom();
        public Task<IAsyncCursor<string>> ListDatabaseNamesAsync(ListDatabaseNamesOptions options, CancellationToken cancellationToken = default) => throw Boom();
        public Task<IAsyncCursor<string>> ListDatabaseNamesAsync(IClientSessionHandle session, CancellationToken cancellationToken = default) => throw Boom();
        public Task<IAsyncCursor<string>> ListDatabaseNamesAsync(IClientSessionHandle session, ListDatabaseNamesOptions options, CancellationToken cancellationToken = default) => throw Boom();
        public IAsyncCursor<BsonDocument> ListDatabases(CancellationToken cancellationToken = default) => throw Boom();
        public IAsyncCursor<BsonDocument> ListDatabases(ListDatabasesOptions options, CancellationToken cancellationToken = default) => throw Boom();
        public IAsyncCursor<BsonDocument> ListDatabases(IClientSessionHandle session, CancellationToken cancellationToken = default) => throw Boom();
        public IAsyncCursor<BsonDocument> ListDatabases(IClientSessionHandle session, ListDatabasesOptions options, CancellationToken cancellationToken = default) => throw Boom();
        public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(CancellationToken cancellationToken = default) => throw Boom();
        public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(ListDatabasesOptions options, CancellationToken cancellationToken = default) => throw Boom();
        public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(IClientSessionHandle session, CancellationToken cancellationToken = default) => throw Boom();
        public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(IClientSessionHandle session, ListDatabasesOptions options, CancellationToken cancellationToken = default) => throw Boom();
        public IClientSessionHandle StartSession(ClientSessionOptions? options = null, CancellationToken cancellationToken = default) => throw Boom();
        public Task<IClientSessionHandle> StartSessionAsync(ClientSessionOptions? options = null, CancellationToken cancellationToken = default) => throw Boom();
        public IChangeStreamCursor<TResult> Watch<TResult>(PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw Boom();
        public IChangeStreamCursor<TResult> Watch<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw Boom();
        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw Boom();
        public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw Boom();
        public IMongoClient WithReadConcern(ReadConcern readConcern) => throw Boom();
        public IMongoClient WithReadPreference(ReadPreference readPreference) => throw Boom();
        public IMongoClient WithWriteConcern(WriteConcern writeConcern) => throw Boom();
        public ClientBulkWriteResult BulkWrite(IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions? options = null, CancellationToken cancellationToken = default) => throw Boom();
        public ClientBulkWriteResult BulkWrite(IClientSessionHandle session, IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions? options = null, CancellationToken cancellationToken = default) => throw Boom();
        public Task<ClientBulkWriteResult> BulkWriteAsync(IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions? options = null, CancellationToken cancellationToken = default) => throw Boom();
        public Task<ClientBulkWriteResult> BulkWriteAsync(IClientSessionHandle session, IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions? options = null, CancellationToken cancellationToken = default) => throw Boom();
        public void Dispose()
        {
            // IMongoClient inherits IDisposable. No state to release in the trip-wire fake.
        }
    }

    private sealed class StubOptionsMonitor : IOptionsMonitor<OctoSystemConfiguration>
    {
        public StubOptionsMonitor(OctoSystemConfiguration current) => Current = current;
        public OctoSystemConfiguration Current { get; }
        public OctoSystemConfiguration CurrentValue => Current;
        public OctoSystemConfiguration Get(string? name) => Current;
        public IDisposable? OnChange(Action<OctoSystemConfiguration, string?> listener) => null;
    }
}
