using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

// AB#8875: the sub-batches of one archive insert run with a bounded concurrency, partitioned by row key. The statements go
// to a fake statement executor (the seam CrateDatabaseClient hands its leased-connection statement to), which records
// what is in flight. The same rows against a real CrateDB: StreamDataWriteLoss/ArchiveWriteConcurrencyIntegrationTests.
public class InsertStatementLanesTests
{
    private const int RowsPerStatement = 1000;
    private const int Limit = 4;

    private static readonly DateTime Origin = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>131 meters x 1,000 quarter hours: the 131 statements of the pv-dorf chunk, every key once.</summary>
    private static readonly string[] ChunkKeys = Enumerable.Range(0, 131_000)
        .Select(i => CrateDatabaseClient.RowKey($"meter-{i % 131}", "Test/Meter", Origin.AddMinutes(15 * (i / 131)),
            Origin.AddMinutes(15 * (i / 131 + 1))))
        .ToArray();

    /// <summary>6,000 rows over 1,500 keys: every key is written four times, 1,500 rows apart.</summary>
    private static readonly string[] RepeatedKeys = Enumerable.Range(0, 6_000)
        .Select(i => CrateDatabaseClient.RowKey($"meter-{i % 1_500}", "Test/Meter", Origin, Origin.AddMinutes(15)))
        .ToArray();

    private static int[] Positions(int count) => Enumerable.Range(0, count).ToArray();

    /// <summary>
    /// Records every statement it is handed: rows in flight per key, the most statements in flight, and the start order
    /// of the rows of each key. A statement waits until <paramref name="holdUntilInFlight" /> statements are in flight
    /// (the first one waits at most 5 s), so a write that runs them one after another cannot reach it.
    /// </summary>
    private sealed class Statements(string[] keys, int holdUntilInFlight = 1)
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, byte> _keysInFlight = new(StringComparer.Ordinal);
        private readonly object _order = new();
        private int _inFlight;
        private int _maxInFlight;

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);
        public ConcurrentBag<string> KeysInTwoStatements { get; } = [];
        public Dictionary<string, List<int>> StartOrderPerKey { get; } = new(StringComparer.Ordinal);
        public ConcurrentBag<int[]> Written { get; } = [];
        public Func<int, bool> Skips { get; init; } = _ => false;

        public async Task<IReadOnlyList<SkippedArchiveRow>> WriteAsync(int[] rows)
        {
            var statementKeys = rows.Select(r => keys[r]).Distinct(StringComparer.Ordinal).ToList();
            foreach (var key in statementKeys)
            {
                if (!_keysInFlight.TryAdd(key, 0))
                {
                    KeysInTwoStatements.Add(key);
                }
            }

            lock (_order)
            {
                foreach (var row in rows)
                {
                    if (!StartOrderPerKey.TryGetValue(keys[row], out var list))
                    {
                        StartOrderPerKey[keys[row]] = list = [];
                    }

                    list.Add(row);
                }
            }

            var inFlight = Interlocked.Increment(ref _inFlight);
            int max;
            while (inFlight > (max = Volatile.Read(ref _maxInFlight)) &&
                   Interlocked.CompareExchange(ref _maxInFlight, inFlight, max) != max)
            {
            }

            if (inFlight >= holdUntilInFlight)
            {
                _held.TrySetResult();
            }

            await Task.WhenAny(_held.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            _held.TrySetResult();
            await Task.Yield();

            foreach (var key in statementKeys)
            {
                _keysInFlight.TryRemove(key, out _);
            }

            Interlocked.Decrement(ref _inFlight);
            Written.Add(rows);
            return rows.Where(Skips).Select(r => new SkippedArchiveRow(r, StreamDataSkipReason.LowerPrecedence)).ToList();
        }
    }

    private static Task<IReadOnlyList<SkippedArchiveRow>> RunAsync(string[] keys, int limit, Statements statements)
        => InsertStatementLanes.RunAsync(
            InsertStatementLanes.Plan(Positions(keys.Length), i => keys[i], RowsPerStatement, limit),
            statements.WriteAsync);

    // AC1, AC2: 131 statements run with the limit in flight, never more.
    [Fact]
    public async Task AChunkOf131Statements_RunsExactlyTheLimitOfStatementsAtOnce()
    {
        var statements = new Statements(ChunkKeys, holdUntilInFlight: Limit);

        await RunAsync(ChunkKeys, Limit, statements);

        Assert.Equal(Limit, statements.MaxInFlight);
        Assert.Equal(Positions(ChunkKeys.Length), statements.Written.SelectMany(s => s).OrderBy(r => r));
    }

    // AC2: two statements in flight never carry the same row key, and the rows of one key are written in the order sent.
    [Fact]
    public async Task RowsOfOneKeyInSeveralStatements_NeverRunAtTheSameTime_AndKeepTheirOrder()
    {
        var statements = new Statements(RepeatedKeys, holdUntilInFlight: Limit);

        await RunAsync(RepeatedKeys, Limit, statements);

        Assert.Equal(Limit, statements.MaxInFlight);
        Assert.Empty(statements.KeysInTwoStatements);
        foreach (var (key, order) in statements.StartOrderPerKey)
        {
            Assert.Equal(Positions(RepeatedKeys.Length).Where(i => RepeatedKeys[i] == key), order);
        }
    }

    // AC2: no statement carries more rows than before, and every row is in exactly one statement.
    [Theory]
    [InlineData(1)]
    [InlineData(Limit)]
    [InlineData(32)]
    public void Plan_PutsEveryRowIntoExactlyOneStatementOfAtMostTheRowsPerStatement(int limit)
    {
        var lanes = InsertStatementLanes.Plan(Positions(ChunkKeys.Length), i => ChunkKeys[i], RowsPerStatement, limit);

        Assert.True(lanes.Count <= limit);
        var statements = lanes.SelectMany(l => l).ToList();
        Assert.All(statements, s => Assert.InRange(s.Length, 1, RowsPerStatement));
        Assert.Equal(Positions(ChunkKeys.Length), statements.SelectMany(s => s).OrderBy(r => r));
    }

    // AC1: a limit of one, or none, writes the statements of the sequential write: consecutive rows, in order.
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-3)]
    public void Plan_WithALimitOfOneOrLess_IsTheSequentialSplit(int limit)
    {
        var lanes = InsertStatementLanes.Plan(Positions(2_500), i => ChunkKeys[i], RowsPerStatement, limit);

        var lane = Assert.Single(lanes);
        Assert.Equal([Positions(1_000), Positions(2_000)[1_000..], Positions(2_500)[2_000..]], lane);
    }

    // AC1: a write that fits into one statement stays one statement.
    [Fact]
    public void Plan_AWriteOfOneStatement_StaysOneStatement()
    {
        var lanes = InsertStatementLanes.Plan(Positions(RowsPerStatement), i => ChunkKeys[i], RowsPerStatement, Limit);

        Assert.Equal(Positions(RowsPerStatement), Assert.Single(Assert.Single(lanes)));
    }

    // AC3: the skipped rows of a concurrent write are the ones of the sequential write, in the same order.
    [Fact]
    public async Task SkippedRows_AreTheRowsOfTheSequentialWrite_InTheSameOrder()
    {
        static bool Skips(int row) => row % 7 == 0 || row % 1_000 == 999;

        var sequential = await RunAsync(ChunkKeys, 1, new Statements(ChunkKeys) { Skips = Skips });
        var concurrent = await RunAsync(ChunkKeys, Limit, new Statements(ChunkKeys, Limit) { Skips = Skips });

        Assert.Equal(Positions(ChunkKeys.Length).Where(Skips), sequential.Select(s => s.Index));
        Assert.Equal(sequential, concurrent);
    }

    // Failure contract (decided 2026-11-23): after the first failure no statement starts, the ones in flight finish,
    // the first failure is rethrown unchanged.
    [Fact]
    public async Task AFailedStatement_StopsEveryLane_LetsTheStatementsInFlightFinish_AndIsRethrownUnchanged()
    {
        var lanes = InsertStatementLanes.Plan(Positions(ChunkKeys.Length), i => ChunkKeys[i], RowsPerStatement, Limit);
        var failingStatement = lanes[0][0];
        var failure = StreamDataWriteIncompleteException.AfterRetry("\"t\".\"archive\"", 1_000, 3, "3 rows had no RETURNING row.");
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new ConcurrentBag<int[]>();
        var finished = new ConcurrentBag<int[]>();

        async Task<IReadOnlyList<SkippedArchiveRow>> WriteAsync(int[] rows)
        {
            started.Add(rows);
            if (started.Count == lanes.Count)
            {
                allStarted.TrySetResult();
            }

            if (rows == failingStatement)
            {
                try
                {
                    await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    failed.SetResult();
                }

                throw failure;
            }

            await failed.Task;
            await Task.Delay(100);
            finished.Add(rows);
            return [];
        }

        var thrown = await Assert.ThrowsAsync<StreamDataWriteIncompleteException>(() =>
            InsertStatementLanes.RunAsync(lanes, WriteAsync));

        Assert.Same(failure, thrown);
        Assert.Equal(lanes.Count, started.Count);
        Assert.Equal(lanes.Count - 1, finished.Count);
        Assert.All(lanes, lane => Assert.True(lane.Count > 1));
    }

    [Fact]
    public async Task TwoFailedLanes_RethrowTheFirstFailure_NotAnAggregate()
    {
        var lanes = InsertStatementLanes.Plan(Positions(ChunkKeys.Length), i => ChunkKeys[i], RowsPerStatement, Limit);
        var first = new InvalidOperationException("first");
        var second = new TimeoutException("second");
        var firstThrown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IReadOnlyList<SkippedArchiveRow>> WriteAsync(int[] rows)
        {
            if (rows == lanes[0][0])
            {
                await Task.Yield();
                firstThrown.SetResult();
                throw first;
            }

            await firstThrown.Task;
            await Task.Delay(100);
            if (rows == lanes[1][0])
            {
                throw second;
            }

            return [];
        }

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => InsertStatementLanes.RunAsync(lanes, WriteAsync));

        Assert.Same(first, thrown);
    }

    // AC1 and AC2 at the client: the lanes of an insert follow the configured limit and the row key, not the position.
    [Fact]
    public void PlanInsertStatements_UsesTheConfiguredLimitAndKeepsEveryKeyInOneLane()
    {
        var configuration = new StreamDataConfiguration { ConnectionString = "Host=localhost", MaxConcurrentInsertStatements = 3 };
        var keys = Enumerable.Range(0, 9_000)
            .Select(i => CrateDatabaseClient.RowKey($"meter-{i % 3_000}", "Test/Meter", Origin, Origin.AddMinutes(15)))
            .ToArray();

        var lanes = CrateDatabaseClient.PlanInsertStatements(keys, Positions(keys.Length), key => key, RowsPerStatement,
            configuration);

        Assert.Equal(3, lanes.Count);
        var laneOfKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var lane = 0; lane < lanes.Count; lane++)
        {
            foreach (var row in lanes[lane].SelectMany(statement => statement))
            {
                Assert.Equal(lane, laneOfKey.TryAdd(keys[row], lane) ? lane : laneOfKey[keys[row]]);
            }
        }

        Assert.Equal(3_000, laneOfKey.Count);
        Assert.Equal(keys.Length, lanes.Sum(lane => lane.Sum(statement => statement.Length)));
    }

    // AC1: the default is below the default pool, a limit above the pool is the pool, a limit of zero or less is one.
    [Theory]
    [InlineData(null, 32, 4)]
    [InlineData(100, 32, 32)]
    [InlineData(8, 6, 6)]
    [InlineData(0, 32, 1)]
    [InlineData(-1, 32, 1)]
    public void EffectiveLimit_IsAtLeastOne_AndAtMostThePoolSize(int? configured, int poolSize, int expected)
    {
        var configuration = new StreamDataConfiguration { ConnectionString = "Host=localhost", MaxPoolSize = poolSize };
        if (configured is { } value)
        {
            configuration.MaxConcurrentInsertStatements = value;
        }

        Assert.Equal(expected, CrateDatabaseClient.EffectiveMaxConcurrentInsertStatements(configuration));
    }
}
